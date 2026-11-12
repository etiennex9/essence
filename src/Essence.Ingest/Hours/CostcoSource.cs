using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Essence.Data;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// Costco's gas bars, from its warehouse locator (<see cref="CostcoOptions.Url"/>). Its terms ban automated collection;
/// the owner accepted that risk for two requests a week, and a curated rule (<see cref="CuratedHoursSource"/>) serves
/// when it can't be had. Matched to the feed's Costco stations within 600 m: the feed's point and the locator's can be
/// 540 m apart (Drummondville's), the gas bar being away from the warehouse.
/// </summary>
public sealed class CostcoSource(HoursClient client, IOptions<HoursOptions> options) : IHoursSource
{
    public const string SourceName = "costco";

    /// <summary>Where a person can check a warehouse's hours.</summary>
    public const string Link = "https://www.costco.ca/w/-/locations";

    /// <summary>
    /// The points searched from, each answering its 50 nearest warehouses: Montréal's covers all 18 Québec warehouses
    /// today, Québec City's those of the east if that changes. Two requests a run.
    /// </summary>
    public static readonly IReadOnlyList<(double Latitude, double Longitude)> Points = [(45.50, -73.57), (46.81, -71.21)];

    /// <summary>
    /// The fields of a warehouse that are read, or might be: the rest (its manager's name, its phones, prices, services)
    /// is dropped before anything is kept.
    /// </summary>
    private static readonly string[] KeptFields =
    [
        "stlocID", "locationName", "address1", "city", "state", "country", "zipCode", "latitude", "longitude",
        "hasGasDepartment", "gasStationHours", "upcomingHolidays",
    ];

    public string Name => SourceName;
    public bool Enabled => options.Value.Costco.Enabled;
    public bool ShippedInImage => false;
    public double MatchRadiusM => 600;

    /// <summary>
    /// Asks each point in turn, and keeps one array of the warehouses found, each once (by <c>stlocID</c>) and with only
    /// <see cref="KeptFields"/>: <c>[true, {…}, {…}]</c>, as the locator answers. Any request that fails fails the run.
    /// </summary>
    public async Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var warehouses = new JsonArray { true };
        var seen = new HashSet<long>();
        foreach (var (latitude, longitude) in Points)
        {
            var url = options.Value.Costco.Url
                .Replace("{lat}", latitude.ToString("0.0#####", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{lon}", longitude.ToString("0.0#####", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var body = await client.GetAsync(new Uri(url), cancellationToken);
            foreach (var warehouse in Warehouses(body))
            {
                if (warehouse["stlocID"] is JsonValue number && number.TryGetValue<long>(out var id) && seen.Add(id))
                {
                    var kept = new JsonObject();
                    foreach (var field in KeptFields)
                    {
                        if (warehouse[field] is { } value)
                        {
                            kept[field] = value.DeepClone();
                        }
                    }

                    warehouses.Add(kept);
                }
            }
        }

        var raw = JsonSerializer.SerializeToUtf8Bytes(warehouses);
        var version = HoursVersion.Of(raw);
        return version == cachedVersion ? new HoursFetch.NotModified() : new HoursFetch.Fetched(raw, version);
    }

    /// <summary>
    /// Québec's warehouses with a gas bar, each with its gas bar's week. A week that can't be read for sure leaves the
    /// warehouse out, with a warning: the curated rule serves it meanwhile.
    /// </summary>
    public HoursParse Parse(byte[] raw)
    {
        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warehouse in Warehouses(raw))
        {
            if (!string.Equals(Text(warehouse, "state"), "QC", StringComparison.OrdinalIgnoreCase)
                || Text(warehouse, "country") is { } country && !string.Equals(country, "CA", StringComparison.OrdinalIgnoreCase)
                || warehouse["hasGasDepartment"]?.GetValueKind() != JsonValueKind.True)
            {
                continue;
            }

            var id = Number(warehouse, "stlocID") is { } number ? number.ToString(CultureInfo.InvariantCulture) : null;
            var what = $"Costco #{id ?? "?"} ({Text(warehouse, "locationName") ?? Text(warehouse, "city")})";
            if (id is null || !seen.Add(id))
            {
                warnings.Add($"{what} has no warehouse number, or the same as another; left out.");
                continue;
            }

            if (Number(warehouse, "latitude") is not { } latitude || Number(warehouse, "longitude") is not { } longitude)
            {
                warnings.Add($"{what} has no coordinates; left out.");
                continue;
            }

            // upcomingHolidays is ignored for now: it gives the warehouse's holiday hours, and whether its gas bar follows
            // them isn't known. The holidays' caution (Essence.Hours' QuebecHolidays) covers those days instead.
            if (Week(warehouse) is not { } lines)
            {
                warnings.Add($"{what} gives no gas bar hours; left out.");
                continue;
            }

            if (RuleNormalizer.Week(lines, out var error) is not { } rule)
            {
                warnings.Add($"{what}: the gas bar's hours can't be read for sure ({error}); left out.");
                continue;
            }

            records.Add(new SourceRecord(
                id,
                BrandFamily.Costco,
                latitude,
                longitude,
                HoursMatcher.HouseNumber(Text(warehouse, "address1")),
                StationKey.NormalizePostalCode(Text(warehouse, "zipCode")) is { Length: > 0 } postcode ? postcode : null,
                rule,
                Link,
                CheckedAt: null,
                TemporarilyClosed: false));
        }

        return new HoursParse(records, warnings);
    }

    /// <summary>The warehouses of an answer: a JSON array that starts with <c>true</c>, then one object per warehouse.</summary>
    /// <remarks>The locator labels it <c>text/html</c>; an HTML page instead (a block, an error) is refused here.</remarks>
    private static IEnumerable<JsonObject> Warehouses(byte[] body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HoursFormatException($"Costco's locator didn't answer with JSON: {ex.Message}", ex);
        }

        if (root is not JsonArray array || array.Count == 0 || array[0]?.GetValueKind() != JsonValueKind.True)
        {
            throw new HoursFormatException("Costco's locator answered something else than its list of warehouses.");
        }

        return array.Skip(1).OfType<JsonObject>();
    }

    private static IEnumerable<WeekLine>? Week(JsonObject warehouse)
    {
        if (warehouse["gasStationHours"] is not JsonArray hours || hours.Count == 0)
        {
            return null;
        }

        var lines = new List<WeekLine>();
        foreach (var entry in hours)
        {
            if (entry is not JsonObject line || Text(line, "title") is not { } title || Text(line, "time") is not { } time)
            {
                return null;
            }

            lines.Add(new WeekLine(title, Text(line, "code"), time));
        }

        return lines;
    }

    private static string? Text(JsonObject node, string field) =>
        node[field] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Trim() : null;

    private static double? Number(JsonObject node, string field) =>
        node[field] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;
}
