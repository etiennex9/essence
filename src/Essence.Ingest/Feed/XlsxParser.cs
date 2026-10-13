using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Essence.Data;

namespace Essence.Ingest.Feed;

/// <summary>
/// Turns one of the Régie's dated xlsx snapshots into a <see cref="FeedDocument"/>, with the same rules as <see cref="FeedParser"/>.
/// The first sheet has a header row (Nom, Bannière, Adresse, Région, Code Postal, Latitude, Longitude, Prix {fuel}…), one row per
/// station, then a footer ("Données générées le : … UTC", "Total de stations : N"). There is no status column, and "N/D" stands for
/// both "unavailable" and "not sold" (see <see cref="FeedStation.NotAvailableFuelTypes"/>).
/// </summary>
public static partial class XlsxParser
{
    private const long MaxEntryBytes = 64 * 1024 * 1024;
    private const string FuelColumnPrefix = "prix ";
    private const string NotAvailable = "N/D";

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <param name="generatedAt">The time in the file name, used when the footer doesn't state one.</param>
    public static FeedDocument Parse(byte[] xlsx, DateTimeOffset generatedAt)
    {
        List<SheetRow> rows;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
            var sharedStrings = ReadSharedStrings(zip);
            rows = ReadRows(Load(zip, FirstSheetPath(zip)), sharedStrings);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new FeedFormatException("The xlsx is not a readable workbook.", ex);
        }

        if (rows.Count == 0)
        {
            throw new FeedFormatException("The xlsx sheet is empty.");
        }

        var columns = Columns.FromHeader(rows[0]);
        var warnings = new List<string>();
        var stations = new List<FeedStation>(rows.Count);
        var stationRows = 0;
        var footer = new List<string>();

        foreach (var row in rows.Skip(1))
        {
            // Blank rows and footer lines have at most one value; a station has many.
            var values = row.Cells.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(2).ToList();
            if (values.Count <= 1)
            {
                footer.AddRange(values);
                continue;
            }

            stationRows++;
            if (ParseStation(row, columns, warnings) is { } station)
            {
                stations.Add(station);
            }
        }

        if (stations.Count == 0)
        {
            // Importing an empty file would deactivate every station.
            throw new FeedFormatException("The xlsx has no stations.");
        }

        var total = footer.Select(line => TotalPattern().Match(line)).FirstOrDefault(m => m.Success);
        if (total is not null && int.TryParse(total.Groups[1].Value, CultureInfo.InvariantCulture, out var expected) && expected != stationRows)
        {
            warnings.Add($"The footer says {expected} stations but the sheet has {stationRows} rows.");
        }

        var stated = FooterGeneratedAt(footer);
        if (stated is not null && stated != generatedAt)
        {
            warnings.Add($"The footer says the file was generated at {stated:O} but its name says {generatedAt:O}.");
        }

        return new FeedDocument((stated ?? generatedAt).ToUniversalTime(), stations, warnings);
    }

    /// <summary>"Données générées le : 2026-10-13 à 02:50:00 UTC".</summary>
    private static DateTimeOffset? FooterGeneratedAt(List<string> footer)
    {
        var match = footer.Select(line => GeneratedPattern().Match(line)).FirstOrDefault(m => m.Success);
        return match is not null
            && DateTimeOffset.TryParseExact($"{match.Groups[1].Value} {match.Groups[2].Value}", "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;
    }

    private static FeedStation? ParseStation(SheetRow row, Columns columns, List<string> warnings)
    {
        var location = $"Row {row.Number}";
        var prices = new List<RawPrice>();
        var notAvailable = new List<string>();
        foreach (var (fuelType, column) in columns.Fuels)
        {
            var text = row.Get(column)?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue; // Not sold.
            }

            if (text.Equals(NotAvailable, StringComparison.OrdinalIgnoreCase))
            {
                notAvailable.Add(fuelType);
            }
            else
            {
                prices.Add(new RawPrice(fuelType, text, null));
            }
        }

        return StationRules.Apply(
            new RawStation(
                location,
                row.Get(columns.Name),
                row.Get(columns.Brand),
                null,
                row.Get(columns.Address),
                row.Get(columns.PostalCode),
                row.Get(columns.Region),
                Coordinate(columns.Latitude),
                Coordinate(columns.Longitude),
                prices,
                notAvailable),
            warnings);

        double? Coordinate(int column)
        {
            var text = row.Get(column);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            warnings.Add($"{location}: unparsable coordinate \"{text}\"; ignored.");
            return null;
        }
    }

    /// <summary>The first sheet of the workbook, through <c>xl/workbook.xml</c> and its relationships.</summary>
    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var relationshipId = workbook.Root?.Element(Main + "sheets")?.Element(Main + "sheet")?.Attribute(OfficeRelationships + "id")?.Value
            ?? throw new FeedFormatException("The xlsx workbook has no sheet.");
        var target = Load(zip, "xl/_rels/workbook.xml.rels").Root?
            .Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(r => r.Attribute("Id")?.Value == relationshipId)?
            .Attribute("Target")?.Value
            ?? throw new FeedFormatException($"The xlsx workbook has no relationship {relationshipId}.");

        // Targets are either absolute in the package ("/xl/worksheets/sheet1.xml") or relative to xl/.
        return target.StartsWith('/') ? target[1..] : "xl/" + target;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        if (zip.GetEntry("xl/sharedStrings.xml") is null)
        {
            return [];
        }

        return Load(zip, "xl/sharedStrings.xml").Root?.Elements(Main + "si").Select(Text).ToList() ?? [];
    }

    private static List<SheetRow> ReadRows(XDocument sheet, List<string> sharedStrings)
    {
        var rows = new List<SheetRow>();
        var sheetData = sheet.Root?.Element(Main + "sheetData") ?? throw new FeedFormatException("The xlsx sheet has no data.");
        foreach (var row in sheetData.Elements(Main + "row"))
        {
            var number = int.TryParse(row.Attribute("r")?.Value, CultureInfo.InvariantCulture, out var r) ? r : rows.Count + 1;
            var cells = new Dictionary<int, string>();
            var nextColumn = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                var column = cell.Attribute("r")?.Value is { } reference ? ColumnIndex(reference) : nextColumn;
                nextColumn = column + 1;
                var value = cell.Element(Main + "v")?.Value;
                cells[column] = cell.Attribute("t")?.Value switch
                {
                    "s" when value is not null => sharedStrings[int.Parse(value, CultureInfo.InvariantCulture)],
                    "inlineStr" => cell.Element(Main + "is") is { } inline ? Text(inline) : "",
                    _ => value ?? "",
                };
            }

            rows.Add(new SheetRow(number, cells));
        }

        return rows;
    }

    /// <summary>The text of a shared or inline string: a plain <c>t</c>, or rich-text runs (phonetic hints excluded).</summary>
    private static string Text(XElement stringItem)
    {
        if (stringItem.Element(Main + "t") is { } plain)
        {
            return plain.Value;
        }

        var text = new StringBuilder();
        foreach (var run in stringItem.Elements(Main + "r"))
        {
            text.Append(run.Element(Main + "t")?.Value);
        }

        return text.ToString();
    }

    /// <summary>"C12" → 2.</summary>
    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z')
            {
                break;
            }

            index = index * 26 + (c - 'A' + 1);
        }

        return index == 0 ? throw new FormatException($"Invalid cell reference \"{reference}\".") : index - 1;
    }

    private static XDocument Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new FeedFormatException($"The xlsx has no {path}.");
        if (entry.Length > MaxEntryBytes)
        {
            throw new FeedFormatException($"{path} in the xlsx is larger than {MaxEntryBytes} bytes.");
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    [GeneratedRegex(@"Total de stations\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalPattern();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})\D+(\d{2}:\d{2}:\d{2})\s*UTC", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedPattern();

    private sealed record SheetRow(int Number, Dictionary<int, string> Cells)
    {
        public string? Get(int column) => Cells.GetValueOrDefault(column);
    }

    /// <summary>Column indexes, found by header text so that a reordered sheet still parses.</summary>
    private sealed record Columns(
        int Name, int Brand, int Address, int Region, int PostalCode, int Latitude, int Longitude,
        IReadOnlyList<(string FuelType, int Column)> Fuels)
    {
        public static Columns FromHeader(SheetRow header)
        {
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var fuels = new List<(string, int)>();
            foreach (var (column, text) in header.Cells.OrderBy(c => c.Key))
            {
                var key = HeaderKey(text);
                if (key.StartsWith(FuelColumnPrefix, StringComparison.Ordinal))
                {
                    fuels.Add((StationKey.CollapseWhitespace(text)[FuelColumnPrefix.Length..], column));
                }
                else
                {
                    byName.TryAdd(key, column);
                }
            }

            if (fuels.Count == 0)
            {
                throw new FeedFormatException("The xlsx header has no \"Prix …\" columns.");
            }

            return new Columns(
                Find("Nom"), Find("Bannière"), Find("Adresse"), Find("Région"), Find("Code Postal"), Find("Latitude"), Find("Longitude"),
                fuels);

            int Find(string name) => byName.TryGetValue(HeaderKey(name), out var column)
                ? column
                : throw new FeedFormatException($"The xlsx header has no \"{name}\" column.");
        }

        /// <summary>"Code  Postal" and "code postal" match; so do "Région" and "Region".</summary>
        private static string HeaderKey(string text) => StationKey.Fold(StationKey.CollapseWhitespace(text));
    }
}
