using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Essence.Data;
using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// Shell's stations, from its station locator (shellgsllocator.geoapp.me). Its lists give the stations in a box, or
/// clusters of them when there are many, each cluster listed in turn (about 22 lists for Québec); only a station's
/// details give its hours, one request each. So only the stations within reach of one of the feed's Shell stations
/// (<see cref="MatchRadiusM"/>) are asked about, about 390, one every 2 to 3 s (<see cref="HoursPacer"/>): a run takes
/// about 17 minutes, once a week. The forecourt's hours come first (<c>forecourt_opening_hours</c>), else the station's.
/// A station whose details fail twice is left out, not the run, so one broken record can't keep the source failing (and
/// asked again and again); <see cref="FailuresInARow"/> stations failing one after the other fail the run.
/// </summary>
public sealed class ShellSource(HoursClient client, HoursPacer pacer, IHoursStations stations, IOptions<HoursOptions> options) : IHoursSource
{
    public const string SourceName = "shell";

    /// <summary>Where a person can check a station's hours, when the locator gives no page of its own.</summary>
    public const string Link = "https://find.shell.com/ca/fuel";

    /// <summary>
    /// Stations whose details fail one after the other, each asked twice, that fail the run: the locator is down or
    /// refusing the run, and asking on would only hammer it.
    /// </summary>
    public const int FailuresInARow = 3;

    /// <summary>How Shell's locator names the province.</summary>
    private const string Quebec = "Quebec";

    private const string TwentyFourHours = "twenty_four_hour";

    /// <summary>Added around a cluster's bounds when it's listed, so its stations on the edges are in.</summary>
    private const double ClusterMarginDegrees = 0.0005;

    /// <summary>The fields of a station's details that are read, or might be: its phone number, services and prices are dropped.</summary>
    private static readonly string[] KeptFields =
    [
        "id", "lat", "lng", "brand", "inactive", "country_code", "address", "city", "state", "postcode", "website_url",
        "opening_hours", "forecourt_opening_hours",
    ];

    /// <summary>Kept only when they say the station is open 24 hours: else they say whether it's open now, which changes by the hour.</summary>
    private static readonly string[] KeptStatuses = ["open_status", "forecourt_open_status"];

    public string Name => SourceName;
    public bool Enabled => options.Value.Shell.Enabled;
    public bool ShippedInImage => false;
    public double MatchRadiusM => 150;

    /// <summary>
    /// Lists Québec's stations, then asks for the details of those within reach of a feed Shell station, and keeps them:
    /// <c>{"locations": […], "missing": [ids], "failed": [{"id", "error"}]}</c>, <c>missing</c> being those the locator no
    /// longer has (a 404). A detail that fails otherwise (an error, no answer in time, something else than a station) is
    /// asked again once, after a pause; failing twice, it's in <c>failed</c>. Any list failing fails the run, and so do
    /// <see cref="FailuresInARow"/> details failing one after the other.
    /// </summary>
    public async Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var o = options.Value.Shell;
        var paced = new Paced(client, pacer);
        var sites = await ListAsync(paced, o, cancellationToken);

        var shells = (await stations.LoadStationsAsync(cancellationToken)).Stations
            .Where(s => s is { BrandFamily: BrandFamily.Shell, Latitude: not null, Longitude: not null })
            .ToList();
        var wanted = sites.Values
            .Where(site => shells.Any(s => HoursMatcher.DistanceM(site.Latitude, site.Longitude, s.Latitude!.Value, s.Longitude!.Value) <= MatchRadiusM))
            .Select(site => site.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        var details = new JsonArray();
        var missing = new JsonArray();
        var failed = new JsonArray();
        var inARow = new List<string>();
        foreach (var id in wanted)
        {
            var url = new Uri(o.DetailUrl.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal));
            try
            {
                if (await DetailAsync(paced, url, cancellationToken) is { } detail)
                {
                    details.Add(Keep(detail));
                }
                else
                {
                    missing.Add(id);
                }

                inARow.Clear();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed.Add(new JsonObject { ["id"] = id, ["error"] = ex.Message });
                inARow.Add(id);
                if (inARow.Count == FailuresInARow)
                {
                    throw new HttpRequestException(
                        $"Shell's locator failed the details of {FailuresInARow} stations in a row ({string.Join(", ", inARow)}), each asked twice: {ex.Message}",
                        ex);
                }
            }
        }

        var raw = HoursJson.Bytes(new JsonObject { ["locations"] = details, ["missing"] = missing, ["failed"] = failed });
        var version = HoursVersion.Of(raw);
        return version == cachedVersion ? new HoursFetch.NotModified() : new HoursFetch.Fetched(raw, version);
    }

    /// <summary>
    /// Québec's stations, each with its forecourt's week (the station's when the forecourt has none). A week that can't be
    /// read for sure leaves the station out, with a warning, and so does a station the locator no longer had or whose
    /// details failed.
    /// </summary>
    public HoursParse Parse(byte[] raw)
    {
        var answer = HoursJson.Parse(raw, "Shell's locator") as JsonObject;
        if (answer?["locations"] is not JsonArray locations)
        {
            throw new HoursFormatException("Shell's details kept on disk aren't a list of stations.");
        }

        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        foreach (var id in (answer["missing"] as JsonArray ?? []).Select(Text))
        {
            warnings.Add($"Shell {id}: the locator listed it, but no longer had its details (404); left out.");
        }

        foreach (var failure in (answer["failed"] as JsonArray ?? []).OfType<JsonObject>())
        {
            warnings.Add($"Shell {HoursJson.Text(failure, "id")}: the locator listed it, but its details failed twice ({HoursJson.Text(failure, "error")}); left out.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in locations.OfType<JsonObject>())
        {
            if (!IsQuebec(site) || HoursJson.IsTrue(site, "inactive"))
            {
                continue;
            }

            var id = HoursJson.Text(site, "id");
            var what = $"Shell {id ?? "?"} ({HoursJson.Text(site, "address")}, {HoursJson.Text(site, "city")})";
            if (id is null || !seen.Add(id))
            {
                warnings.Add($"{what} has no id, or the same as another; left out.");
                continue;
            }

            var latitude = HoursJson.Number(site, "lat");
            var longitude = HoursJson.Number(site, "lng");
            if (!HoursJson.IsPoint(latitude, longitude))
            {
                warnings.Add($"{what} has no coordinates; left out.");
                continue;
            }

            if (Rule(site, out var error) is not { } rule)
            {
                warnings.Add($"{what}: the hours can't be read for sure ({error}); left out.");
                continue;
            }

            var page = HoursJson.Text(site, "website_url");
            records.Add(new SourceRecord(
                id,
                BrandFamily.Of(HoursJson.Text(site, "brand")) ?? BrandFamily.Shell,
                latitude,
                longitude,
                HoursMatcher.HouseNumber(HoursJson.Text(site, "address")),
                StationKey.NormalizePostalCode(HoursJson.Text(site, "postcode")) is { Length: > 0 } postcode ? postcode : null,
                rule,
                Uri.TryCreate(page, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? page : Link,
                CheckedAt: null,
                TemporarilyClosed: false));
        }

        return new HoursParse(records, warnings);
    }

    /// <summary>
    /// The week: each entry's <c>days</c> are one day (<c>["Fri"]</c>) or a range (<c>["Mon", "Thu"]</c>, across the
    /// weekend when it wraps), and its <c>hours</c> one or more ranges (<c>[["04:45", "21:30"]]</c>), every day given
    /// once. A station whose status says it's open 24 hours is <c>24/7</c>, its days saying so too when it gives any.
    /// </summary>
    private static string? Rule(JsonObject site, out string? error)
    {
        var (groups, status) = site["forecourt_opening_hours"] is JsonArray { Count: > 0 } forecourt
            ? (forecourt, HoursJson.Text(site, "forecourt_open_status"))
            : site["opening_hours"] is JsonArray { Count: > 0 } station
                ? (station, HoursJson.Text(site, "open_status"))
                : ([], HoursJson.Text(site, "forecourt_open_status") ?? HoursJson.Text(site, "open_status"));
        var allDay = status == TwentyFourHours;
        if (groups.Count == 0)
        {
            error = allDay ? null : "it gives none";
            return allDay ? OpeningHours.AlwaysOpen.ToRule() : null;
        }

        var week = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>();
        foreach (var node in groups)
        {
            if (node is not JsonObject group || Days(group["days"]) is not { } days)
            {
                error = $"the days {(node as JsonObject)?["days"]?.ToJsonString() ?? "(none)"} aren't understood";
                return null;
            }

            if (Ranges(group["hours"]) is not { } ranges)
            {
                error = $"the hours {group["hours"]?.ToJsonString() ?? "(none)"} aren't understood";
                return null;
            }

            foreach (var day in days)
            {
                if (!week.TryAdd(day, ranges))
                {
                    error = $"{day}'s hours are given twice";
                    return null;
                }
            }
        }

        var rule = RuleNormalizer.Week(week, out error);
        if (rule is not null && allDay && rule != OpeningHours.AlwaysOpen.ToRule())
        {
            error = $"it says it's open 24 hours, but its days say {rule}";
            return null;
        }

        return rule;
    }

    /// <summary><c>["Sat", "Sun"]</c> is Saturday to Sunday, <c>["Mon", "Sun"]</c> every day, <c>["Fri"]</c> Friday.</summary>
    private static IReadOnlyList<DayOfWeek>? Days(JsonNode? node)
    {
        if (node is not JsonArray { Count: 1 or 2 } names
            || !RuleNormalizer.TryDay(Text(names[0]), out var first) || !RuleNormalizer.TryDay(Text(names[^1]), out var last))
        {
            return null;
        }

        return RuleNormalizer.Range(first, last);
    }

    /// <summary><c>[["05:00", "23:00"]]</c>, or several ranges; an empty list isn't taken as closed.</summary>
    private static IReadOnlyList<TimeRange>? Ranges(JsonNode? node)
    {
        if (node is not JsonArray { Count: > 0 } pairs)
        {
            return null;
        }

        var ranges = new List<TimeRange>();
        foreach (var pair in pairs)
        {
            if (pair is not JsonArray { Count: 2 } times || !RuleNormalizer.TryRange(Text(times[0]), Text(times[1]), out var range))
            {
                return null;
            }

            ranges.Add(range);
        }

        return ranges;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Trim() : null;

    private static bool IsQuebec(JsonObject site) =>
        string.Equals(HoursJson.Text(site, "state"), Quebec, StringComparison.OrdinalIgnoreCase)
        && HoursJson.Text(site, "country_code") is null or "CA";

    /// <summary>
    /// The stations in Québec's box, listed down to the stations: a list that answers clusters has each cluster's bounds
    /// listed in turn, and one that answers itself back (a single cluster as big as the one asked about) is split in four.
    /// </summary>
    private static async Task<Dictionary<string, ListedSite>> ListAsync(Paced paced, ShellOptions o, CancellationToken cancellationToken)
    {
        var sites = new Dictionary<string, ListedSite>(StringComparer.Ordinal);
        var boxes = new Queue<(GeoBox Box, int? Size)>([(o.Box, null)]);
        var requests = 0;
        while (boxes.TryDequeue(out var next))
        {
            if (++requests > o.MaxListRequests)
            {
                throw new HoursFormatException(
                    $"Shell's locator still answered clusters after {o.MaxListRequests} lists (Hours:Shell:MaxListRequests).");
            }

            var answer = HoursJson.Parse(await paced.GetAsync(new Uri(next.Box.Fill(o.ListUrl)), cancellationToken), "Shell's locator") as JsonObject;
            if (answer?["locations"] is not JsonArray locations || answer["clusters"] is not JsonArray clusters)
            {
                throw new HoursFormatException("Shell's locator answered something else than its list of stations.");
            }

            foreach (var site in locations.OfType<JsonObject>())
            {
                if (IsQuebec(site) && !HoursJson.IsTrue(site, "inactive") && BrandFamily.Of(HoursJson.Text(site, "brand")) == BrandFamily.Shell
                    && HoursJson.Text(site, "id") is { } id
                    && HoursJson.Number(site, "lat") is { } latitude && HoursJson.Number(site, "lng") is { } longitude)
                {
                    sites.TryAdd(id, new ListedSite(id, latitude, longitude));
                }
            }

            var parts = clusters.OfType<JsonObject>().Select(Cluster).ToList();
            if (parts.Any(p => p is null))
            {
                throw new HoursFormatException("Shell's locator answered a cluster without its bounds.");
            }

            if (parts is [{ } only] && next.Size is { } size && only.Size >= size)
            {
                parts = [.. Quarters(next.Box).Select(q => ((GeoBox, int))(q, size))];
            }

            foreach (var part in parts)
            {
                boxes.Enqueue(part!.Value);
            }
        }

        return sites;
    }

    /// <summary>A cluster's bounds (<c>{"sw": [lat, lng], "ne": [lat, lng]}</c>), with a margin, and how many stations it has.</summary>
    private static (GeoBox Box, int Size)? Cluster(JsonObject cluster)
    {
        if (cluster["bounds"] is not JsonObject bounds || Corner(bounds["sw"]) is not { } sw || Corner(bounds["ne"]) is not { } ne)
        {
            return null;
        }

        const double margin = ClusterMarginDegrees;
        var size = HoursJson.Number(cluster, "size") is { } count ? (int)count : 0;
        return (new GeoBox(ne.Latitude + margin, sw.Longitude - margin, sw.Latitude - margin, ne.Longitude + margin), size);

        static (double Latitude, double Longitude)? Corner(JsonNode? node) =>
            node is JsonArray { Count: 2 } corner && corner[0] is JsonValue a && a.TryGetValue<double>(out var latitude)
            && corner[1] is JsonValue b && b.TryGetValue<double>(out var longitude)
                ? (latitude, longitude)
                : null;
    }

    private static IEnumerable<GeoBox> Quarters(GeoBox box)
    {
        var middleLatitude = (box.North + box.South) / 2;
        var middleLongitude = (box.West + box.East) / 2;
        yield return new GeoBox(box.North, box.West, middleLatitude, middleLongitude);
        yield return new GeoBox(box.North, middleLongitude, middleLatitude, box.East);
        yield return new GeoBox(middleLatitude, box.West, box.South, middleLongitude);
        yield return new GeoBox(middleLatitude, middleLongitude, box.South, box.East);
    }

    /// <summary>
    /// A station's details, or null when the locator no longer has it; a failure, a body that isn't a station's included
    /// (a block page), is tried again once, and the second one thrown.
    /// </summary>
    private static async Task<JsonObject?> DetailAsync(Paced paced, Uri url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return HoursJson.Parse(await paced.GetAsync(url, cancellationToken), "Shell's locator") as JsonObject
                    ?? throw new HoursFormatException($"Shell's locator answered something else than a station's details at {url}.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (Exception) when (attempt == 1 && !cancellationToken.IsCancellationRequested)
            {
                // Once more, after the pause every request waits.
            }
        }
    }

    private static JsonObject Keep(JsonObject detail)
    {
        var kept = HoursJson.Keep(detail, KeptFields);
        foreach (var status in KeptStatuses)
        {
            if (HoursJson.Text(detail, status) == TwentyFourHours)
            {
                kept[status] = TwentyFourHours;
            }
        }

        return kept;
    }

    private sealed record ListedSite(string Id, double Latitude, double Longitude);

    /// <summary>The run's requests to Shell, a pause before each but the first.</summary>
    private sealed class Paced(HoursClient client, HoursPacer pacer)
    {
        private bool _first = true;

        public async Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            if (!_first)
            {
                await pacer.PauseAsync(cancellationToken);
            }

            _first = false;
            return await client.GetAsync(url, cancellationToken);
        }
    }
}
