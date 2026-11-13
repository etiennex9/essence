using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Essence.Data;
using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// OpenStreetMap's fuel stations in Québec (<c>amenity=fuel</c>, about 2,860 elements), from an Overpass server: one
/// query a run, to the next server in <see cref="OsmOptions.Endpoints"/> when one fails (busy, a 504, too slow, an error in
/// its answer, or no elements at all: a server without its areas, or rebuilding them, answers so). The data is © OpenStreetMap contributors, under the ODbL: where the app shows these hours it says
/// OpenStreetMap, with a link to the element.
/// <para>
/// Every element is a record, its hours or not (about 184 have <c>opening_hours</c>), so a station knows which element
/// to edit; hours that can't be read for sure, even leniently, leave the record without them, with a warning. Matched to
/// the feed's stations of the same brand (<c>brand</c>, else <c>operator</c>, else <c>name</c>) within 150 m, confirmed by
/// the postal code or the house number. OpenStreetMap's hours are shown, never trusted to leave a station out.
/// </para>
/// <para>
/// The brand is kept only when some station of the feed has it: an operator or a name can be a person's ("Garage
/// Untel"), and an element of a brand no station has can't match one anyway.
/// </para>
/// </summary>
public sealed class OverpassSource(OverpassClient client, IHoursStations stations, IOptions<HoursOptions> options) : IHoursSource
{
    public const string SourceName = "osm";

    /// <summary>Where an element is seen, and edited: <c>https://www.openstreetmap.org/node/6756481766</c>.</summary>
    public const string ElementLink = "https://www.openstreetmap.org/";

    /// <summary>The element's brand: the first of these it has.</summary>
    private static readonly string[] BrandTags = ["brand", "operator", "name"];

    /// <summary>The tags read besides its brand; the rest (phones, websites, who mapped it) is never kept.</summary>
    private static readonly string[] KeptTags = ["opening_hours", "addr:housenumber", "addr:postcode", "check_date:opening_hours", "check_date"];

    private static readonly string[] ElementTypes = ["node", "way", "relation"];

    /// <summary>When the hours were checked, the first of these the element has.</summary>
    private static readonly string[] CheckTags = ["check_date:opening_hours", "check_date"];

    public string Name => SourceName;
    public bool Enabled => options.Value.Osm.Enabled;
    public bool ShippedInImage => false;
    public double MatchRadiusM => 150;

    /// <summary>
    /// Asks each server in turn until one answers with elements, and keeps them with only their point and the tags read
    /// (the brand only when it's a feed station's): <c>{"osm3s": {"timestamp_osm_base": …}, "elements": […]}</c>, as
    /// Overpass answers. The servers all failing fails the run, the last one's failure inside.
    /// </summary>
    /// <exception cref="HttpRequestException">No server answered with the elements.</exception>
    public async Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var o = options.Value.Osm;
        var brands = (await stations.LoadStationsAsync(cancellationToken)).Stations
            .Select(s => s.BrandFamily)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();
        Exception? last = null;
        foreach (var endpoint in o.EndpointList)
        {
            try
            {
                var answer = Answer(await client.PostAsync(new Uri(endpoint), o.Query, cancellationToken));
                if (!Elements(answer).Any())
                {
                    // Québec never has no fuel station: the server can't run the query (its areas are missing or rebuilding).
                    throw new HoursFormatException("Overpass answered no elements at all.");
                }

                var raw = HoursJson.Bytes(Keep(answer, brands));
                var version = HoursVersion.Of(raw);
                return version == cachedVersion ? new HoursFetch.NotModified() : new HoursFetch.Fetched(raw, version);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
                failures.Add($"{endpoint}: {ex.Message}");
            }
        }

        throw new HttpRequestException($"No Overpass server answered: {string.Join("; ", failures)}", last);
    }

    public HoursParse Parse(byte[] raw)
    {
        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Elements(Answer(raw)))
        {
            if (Reference(element) is not { } reference || !seen.Add(reference))
            {
                continue;
            }

            var (latitude, longitude) = Point(element);
            if (!HoursJson.IsPoint(latitude, longitude))
            {
                warnings.Add($"OpenStreetMap {reference} has no point; left out.");
                continue;
            }

            var tags = element["tags"] as JsonObject ?? [];
            string? rule = null;
            if (HoursJson.Text(tags, "opening_hours") is { } text)
            {
                if (OpeningHours.TryParse(text, lenient: true, out var hours, out var error))
                {
                    rule = hours.ToRule();
                }
                else
                {
                    warnings.Add($"OpenStreetMap {reference}: its opening_hours \"{text}\" can't be read for sure ({error}); kept without them.");
                }
            }

            records.Add(new SourceRecord(
                reference,
                BrandFamily.Of(BrandTags.Select(t => HoursJson.Text(tags, t)).FirstOrDefault(b => b is not null)),
                latitude,
                longitude,
                HoursMatcher.HouseNumber(HoursJson.Text(tags, "addr:housenumber")),
                StationKey.NormalizePostalCode(HoursJson.Text(tags, "addr:postcode")) is { Length: > 0 } postcode ? postcode : null,
                rule,
                ElementLink + reference,
                CheckedAt(tags),
                TemporarilyClosed: false));
        }

        return new HoursParse(records, warnings);
    }

    /// <summary>
    /// When the hours were last checked: <c>check_date:opening_hours</c>, else <c>check_date</c> (the whole element's), as a
    /// day (<c>2026-09-15</c>) or a month (<c>2026-09</c>); its midnight in Québec. The element's last edit isn't a check:
    /// it may have changed anything else.
    /// </summary>
    private static DateTimeOffset? CheckedAt(JsonObject tags)
    {
        foreach (var tag in CheckTags)
        {
            var text = HoursJson.Text(tags, tag);
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || DateOnly.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            {
                return CuratedHoursSource.MidnightInQuebec(day);
            }
        }

        return null;
    }

    /// <summary><c>node/123</c>, <c>way/456</c>: how OpenStreetMap names an element.</summary>
    private static string? Reference(JsonObject element) =>
        HoursJson.Text(element, "type") is { } type && ElementTypes.Contains(type)
        && element["id"] is JsonValue id && id.TryGetValue<long>(out var number) && number > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{type}/{number}")
            : null;

    /// <summary>A node's point, or a way's or a relation's centre (<c>out center</c>).</summary>
    private static (double? Latitude, double? Longitude) Point(JsonObject element) =>
        element["center"] is JsonObject center
            ? (HoursJson.Number(center, "lat"), HoursJson.Number(center, "lon"))
            : (HoursJson.Number(element, "lat"), HoursJson.Number(element, "lon"));

    /// <summary>
    /// Overpass's answer, refused when it says it failed: a query that runs out of time or memory still answers 200, with
    /// a <c>remark</c> saying so and the elements found until then.
    /// </summary>
    private static JsonObject Answer(byte[] body)
    {
        if (HoursJson.Parse(body, "Overpass") is not JsonObject answer || answer["elements"] is not JsonArray)
        {
            throw new HoursFormatException("Overpass answered something else than elements.");
        }

        if (HoursJson.Text(answer, "remark") is { } remark && remark.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            throw new HoursFormatException($"Overpass's query failed: {remark}");
        }

        return answer;
    }

    private static IEnumerable<JsonObject> Elements(JsonObject answer) => ((JsonArray)answer["elements"]!).OfType<JsonObject>();

    /// <summary>
    /// The elements with their point, <see cref="KeptTags"/> and their brand's tag when it's one of <paramref name="brands"/>,
    /// and when the data is from.
    /// </summary>
    private static JsonObject Keep(JsonObject answer, IReadOnlySet<string> brands)
    {
        var elements = new JsonArray();
        foreach (var element in Elements(answer))
        {
            var kept = HoursJson.Keep(element, ["type", "id", "lat", "lon", "center"]);
            if (element["tags"] is JsonObject tags)
            {
                var brand = BrandTags.FirstOrDefault(t => HoursJson.Text(tags, t) is not null);
                var known = brand is not null && BrandFamily.Of(HoursJson.Text(tags, brand)) is { } family && brands.Contains(family);
                kept["tags"] = HoursJson.Keep(tags, known ? [brand!, .. KeptTags] : KeptTags);
            }

            elements.Add(kept);
        }

        var meta = answer["osm3s"] as JsonObject ?? [];
        return new JsonObject
        {
            ["osm3s"] = HoursJson.Keep(meta, ["timestamp_osm_base", "copyright"]),
            ["elements"] = elements,
        };
    }
}
