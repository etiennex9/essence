using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using Essence.Data;
using Essence.Ingest.Feed;

namespace Essence.Ingest.Tests;

/// <summary>Builders for feed files and parsed stations.</summary>
internal static class TestFeeds
{
    public static readonly DateTimeOffset GeneratedAt = new(2026, 10, 12, 12, 0, 1, TimeSpan.Zero);

    /// <summary>generated_at of the JSON/xlsx pair, to the second (the JSON says 03:05:01.1489389).</summary>
    public static readonly DateTimeOffset PairGeneratedAt = new(2026, 10, 13, 3, 5, 1, TimeSpan.Zero);

    public static readonly string[] XlsxHeader =
        ["Nom", "Bannière", "Adresse", "Région", "Code Postal", "Latitude", "Longitude", "Prix Régulier", "Prix Super", "Prix Diesel"];

    public static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations.sample.geojson"));

    /// <summary>The live JSON and the xlsx its excel_url named, downloaded together: the same version in both formats.</summary>
    public static byte[] PairJson() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pair", "stations-20261013030501.geojson"));

    /// <inheritdoc cref="PairJson"/>
    public static byte[] PairXlsx() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pair", "stations-20261013030501.xlsx"));

    /// <summary>
    /// A minimal workbook with one sheet. Cells: a string becomes an inline string, a double a number, null no cell at all.
    /// The sheet relationship is relative ("worksheets/sheet1.xml") and there are no shared strings, unlike the Régie's files.
    /// </summary>
    public static byte[] Xlsx(params object?[][] rows)
    {
        var sheet = new StringBuilder("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}">""");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var reference = $"{(char)('A' + c)}{r + 1}";
                switch (rows[r][c])
                {
                    case null:
                        break;
                    case double number:
                        sheet.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}"><v>{number}</v></c>""");
                        break;
                    case var text:
                        sheet.Append($"""<c r="{reference}" t="inlineStr"><is><t>{SecurityElement.Escape(text.ToString())}</t></is></c>""");
                        break;
                }
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Stations" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", sheet.ToString());

            void Add(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        }

        return output.ToArray();
    }

    /// <summary>A feed file with one station per address, each selling Régulier at <paramref name="price"/>.</summary>
    public static byte[] Json(DateTimeOffset generatedAt, decimal price = 199.4m, params string[] addresses)
    {
        if (addresses.Length == 0)
        {
            addresses = ["230 av. Murdoch, Rouyn-Noranda"];
        }

        var document = new
        {
            type = "FeatureCollection",
            metadata = new { generated_at = generatedAt, total_stations = addresses.Length },
            features = addresses.Select(address => new
            {
                type = "Feature",
                geometry = new { type = "Point", coordinates = new[] { -79.019574, 48.246759 } },
                properties = new
                {
                    Name = "Station",
                    brand = "Shell",
                    Status = "En opération",
                    Address = address,
                    PostalCode = "J9X 1E7",
                    Region = "Abitibi-Témiscamingue",
                    Prices = new[] { new { GasType = "Régulier", Price = price.ToString("0.0", CultureInfo.InvariantCulture) + "¢", IsAvailable = true } },
                },
            }),
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    /// <summary>A feed file with a single station whose properties are given as raw JSON.</summary>
    public static byte[] RawStation(string propertiesJson, string coordinates = "[-73.5, 45.5]") =>
        Encoding.UTF8.GetBytes($$"""
            {
              "type": "FeatureCollection",
              "metadata": { "generated_at": "2026-10-12T12:00:01.1234567Z", "total_stations": 1 },
              "features": [ { "type": "Feature", "geometry": { "type": "Point", "coordinates": {{coordinates}} }, "properties": {{propertiesJson}} } ]
            }
            """);

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    public static FeedStation Station(
        string address,
        string? brand = "Shell",
        FeedPrice[]? prices = null,
        string[]? skippedFuelTypes = null,
        string? status = "En opération",
        string[]? notAvailableFuelTypes = null) =>
        new(
            StationKey.Create(address, "H2Z 1W7"),
            "Station",
            brand,
            status,
            address,
            "H2Z 1W7",
            "Montréal",
            45.5,
            -73.5,
            prices ?? [Price("Régulier", 199.4m)],
            skippedFuelTypes ?? [],
            notAvailableFuelTypes ?? []);

    public static FeedPrice Price(string fuelType, decimal? cents, bool? isAvailable = null) =>
        new(fuelType, cents, isAvailable ?? cents is not null);

    public static FeedDocument Document(params FeedStation[] stations) => new(GeneratedAt, stations, []);
}
