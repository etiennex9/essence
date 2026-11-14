using System.Text.Json;
using System.Text.Json.Nodes;
using Essence.Data;
using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// Ultramar's stations, from its locator on journie.ca (<see cref="UltramarOptions.Url"/>): one request for all of
/// Canada's, about 620, 364 in Québec. Its robots.txt allows everything. Each station gives its hours, and each of its
/// services (the forecourt, the store, the car wash…) its own: the forecourt's, the pumps', come first. Each has a
/// status too, "active" or "temporary closure", but the forecourt's says little: it was active at every Québec station
/// in November 2026, the 7 closed for now ("temporary closure", their store's too) included, and some of those sold gas
/// while others' prices hadn't moved in a month. So a station closed for now whose forecourt is active is left out,
/// with a warning: which is right can't be told. Matched to the feed's Ultramar stations within 150 m, or within 2 km at
/// the same house number and postal code (<see cref="HoursMatcher.AddressReachM"/>): the locator puts a few stations a
/// street or more from where they are.
/// </summary>
public sealed class UltramarSource(HoursClient client, IOptions<HoursOptions> options) : IHoursSource
{
    public const string SourceName = "ultramar";

    /// <summary>Where a person can check a station's hours.</summary>
    public const string Link = "https://journie.ca/ultramar/qc-fr";

    private const string Active = "active";
    private const string TemporaryClosure = "temporary closure";
    private const string Forecourt = "Forecourt";

    /// <summary>The fields of a station that are read, or might be; the rest (its services' features) is dropped.</summary>
    private static readonly string[] KeptFields =
    [
        "locationId", "coordinates", "siteStatus", "siteAddressLine1", "siteCity", "siteStateProvince", "sitePostalCode",
        "siteCountry", "siteTwentyFourSeven", "siteRegularHours", "services",
    ];

    private static readonly string[] KeptServiceFields = ["serviceType", "serviceStatus", "serviceTwentyFourSeven", "serviceRegularHours"];

    public string Name => SourceName;
    public bool Enabled => options.Value.Ultramar.Enabled;
    public bool ShippedInImage => false;
    public double MatchRadiusM => 150;
    public double? OtherBrandReachM => null;

    /// <summary>
    /// Asks once, and keeps the answer's stations with only <see cref="KeptFields"/>, as the locator answers:
    /// <c>{"data": […]}</c>. An answer with more pages than the one asked is refused: stations would be missing.
    /// </summary>
    public async Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var answer = Answer(await client.GetAsync(new Uri(options.Value.Ultramar.Url), cancellationToken));
        if (answer["pagination"] is JsonObject pagination && HoursJson.IsTrue(pagination, "hasNextPage"))
        {
            throw new HoursFormatException("Ultramar's locator has more stations than a page holds: raise pageSize in Hours:Ultramar:Url.");
        }

        var sites = new JsonArray();
        foreach (var site in Sites(answer))
        {
            var kept = HoursJson.Keep(site, KeptFields);
            if (site["services"] is JsonArray services)
            {
                kept["services"] = new JsonArray([.. services.OfType<JsonObject>().Select(s => HoursJson.Keep(s, KeptServiceFields))]);
            }

            sites.Add(kept);
        }

        var raw = HoursJson.Bytes(new JsonObject { ["data"] = sites });
        var version = HoursVersion.Of(raw);
        return version == cachedVersion ? new HoursFetch.NotModified() : new HoursFetch.Fetched(raw, version);
    }

    /// <summary>
    /// Québec's stations, each with its forecourt's week (the station's when it has no forecourt) and whether it's closed
    /// for now: when its forecourt is, or the station is and has no forecourt. A week that can't be read for sure leaves
    /// the station out, with a warning, and so does a status that isn't understood or a station closed for now whose
    /// forecourt is active.
    /// </summary>
    public HoursParse Parse(byte[] raw)
    {
        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in Sites(Answer(raw)))
        {
            if (!string.Equals(HoursJson.Text(site, "siteStateProvince"), "QC", StringComparison.OrdinalIgnoreCase)
                || HoursJson.Text(site, "siteCountry") is { } country && !string.Equals(country, "CA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = HoursJson.Text(site, "locationId");
            var what = $"Ultramar {id ?? "?"} ({HoursJson.Text(site, "siteAddressLine1")}, {HoursJson.Text(site, "siteCity")})";
            if (id is null || !seen.Add(id))
            {
                warnings.Add($"{what} has no id, or the same as another; left out.");
                continue;
            }

            var coordinates = site["coordinates"] as JsonObject ?? [];
            var latitude = HoursJson.Number(coordinates, "latitude");
            var longitude = HoursJson.Number(coordinates, "longitude");
            if (!HoursJson.IsPoint(latitude, longitude))
            {
                warnings.Add($"{what} has no coordinates; left out.");
                continue;
            }

            var forecourt = (site["services"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(s => string.Equals(HoursJson.Text(s, "serviceType"), Forecourt, StringComparison.OrdinalIgnoreCase));
            var siteStatus = HoursJson.Text(site, "siteStatus");
            var forecourtStatus = (forecourt is null ? null : HoursJson.Text(forecourt, "serviceStatus")) ?? siteStatus;
            if (Closed(siteStatus) is not { } siteClosed || Closed(forecourtStatus) is not { } closed)
            {
                warnings.Add($"{what}: its status \"{(Closed(siteStatus) is null ? siteStatus : forecourtStatus)}\" isn't understood; left out.");
                continue;
            }

            if (siteClosed && !closed)
            {
                warnings.Add($"{what}: it says it's closed for now, but its forecourt says it's active: which is right can't be told; left out.");
                continue;
            }

            var fromForecourt = forecourt is not null && Gives(forecourt, "serviceRegularHours", "serviceTwentyFourSeven");
            var (days, allDay) = fromForecourt
                ? (forecourt!["serviceRegularHours"] as JsonArray, HoursJson.IsTrue(forecourt, "serviceTwentyFourSeven"))
                : (site["siteRegularHours"] as JsonArray, HoursJson.IsTrue(site, "siteTwentyFourSeven"));
            if (Rule(days, allDay, out var error) is not { } rule)
            {
                warnings.Add($"{what}: the {(fromForecourt ? "forecourt's" : "station's")} hours can't be read for sure ({error}); left out.");
                continue;
            }

            records.Add(new SourceRecord(
                id,
                BrandFamily.Ultramar,
                latitude,
                longitude,
                HoursMatcher.HouseNumber(HoursJson.Text(site, "siteAddressLine1")),
                StationKey.NormalizePostalCode(HoursJson.Text(site, "sitePostalCode")) is { Length: > 0 } postcode ? postcode : null,
                rule,
                Link,
                CheckedAt: null,
                TemporarilyClosed: closed));
        }

        return new HoursParse(records, warnings);
    }

    /// <summary>
    /// The week from each day's <c>openTime</c> and <c>closeTime</c> ("06:00" to "22:00", "00:00" to "23:59" all day),
    /// every day given. "00:00" to "12:00" is refused: the locator gives it on days a station seems closed (the weekend of
    /// one open from 6 to 18 on weekdays, every day at another), and which it means can't be told. A station said to be
    /// open 24 hours whose days say otherwise is refused too.
    /// </summary>
    private static string? Rule(JsonArray? days, bool allDay, out string? error)
    {
        var week = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>();
        foreach (var entry in days?.OfType<JsonObject>() ?? [])
        {
            var name = HoursJson.Text(entry, "dayOfWeek");
            if (!RuleNormalizer.TryDay(name, out var day))
            {
                error = $"the day \"{name}\" isn't understood";
                return null;
            }

            var (open, close) = (HoursJson.Text(entry, "openTime"), HoursJson.Text(entry, "closeTime"));
            if (open == "00:00" && close == "12:00")
            {
                error = $"{day}'s hours are \"00:00\" to \"12:00\", what the locator gives when it doesn't say";
                return null;
            }

            if (!RuleNormalizer.TryRange(open, close, out var range))
            {
                error = $"{day}'s hours \"{open}\" to \"{close}\" aren't understood";
                return null;
            }

            if (!week.TryAdd(day, [range]))
            {
                error = $"{day}'s hours are given twice";
                return null;
            }
        }

        if (week.Count == 0)
        {
            error = allDay ? null : "it gives none";
            return allDay ? OpeningHours.AlwaysOpen.ToRule() : null;
        }

        var rule = RuleNormalizer.Week(week, out error);
        if (rule is not null && allDay && rule != OpeningHours.AlwaysOpen.ToRule())
        {
            error = $"it says it's open 24 hours, but its days say {rule}";
            return null;
        }

        return rule;
    }

    /// <summary>Whether a status says closed for now ("temporary closure") or not ("active"); null when it's something else.</summary>
    private static bool? Closed(string? status) =>
        string.Equals(status, Active, StringComparison.OrdinalIgnoreCase) ? false
        : string.Equals(status, TemporaryClosure, StringComparison.OrdinalIgnoreCase) ? true
        : null;

    /// <summary>The service gives hours: days, or that it's open 24 hours.</summary>
    private static bool Gives(JsonObject service, string days, string allDay) =>
        service[days] is JsonArray { Count: > 0 } || HoursJson.IsTrue(service, allDay);

    /// <summary>The answer: <c>{"data": […]}</c>, which the locator says succeeded when it says.</summary>
    private static JsonObject Answer(byte[] body) =>
        HoursJson.Parse(body, "Ultramar's locator") is JsonObject answer && answer["data"] is JsonArray
        && answer["success"]?.GetValueKind() is null or JsonValueKind.True
            ? answer
            : throw new HoursFormatException("Ultramar's locator answered something else than its list of stations.");

    private static IEnumerable<JsonObject> Sites(JsonObject answer) => ((JsonArray)answer["data"]!).OfType<JsonObject>();
}
