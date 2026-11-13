using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Essence.Data;
using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// Couche-Tard's Québec stores, from Circle K's store locator (<see cref="CircleKOptions.Url"/>), asked about Québec's west
/// and east: two requests a run, a pause between them. The hours are the store's (<c>openingHours</c>, or
/// <c>openingHoursFuel</c> when a store gives those, which none in Québec did in 2026): 8 stores in 10 are open 24/7.
/// <para>
/// A store finds its station only by the number in the station's name: "Couche-Tard #140" is the store 6000140, whatever
/// the station's brand (Couche-Tard's stores sell Esso's, Irving's, Shell's… gas) and wherever the two points are (8 of
/// 468 are 336 to 716 m apart). Never by distance: the store nearest a station is another station's 74 % of the time.
/// </para>
/// </summary>
public sealed class CircleKSource(HoursClient client, HoursPacer pacer, IOptions<HoursOptions> options) : IHoursSource
{
    public const string SourceName = "circlek";

    /// <summary>Where a person can check a store's hours.</summary>
    public const string Link = "https://www.circlek.com/store-locator";

    /// <summary>Couche-Tard's Québec stores are numbered from here: the store 6000140 is "Couche-Tard #140".</summary>
    public const int StoreNumberBase = 6_000_000;

    /// <summary>The fields of a store that are read, or might be: its phone number, services and prices are dropped.</summary>
    private static readonly string[] KeptFields = ["id", "latitude", "longitude", "address", "openingHours", "openingHoursFuel", "brand"];

    private static readonly string[] KeptAddress = ["street", "city", "state", "postalCode", "country"];

    public string Name => SourceName;
    public bool Enabled => options.Value.CircleK.Enabled;
    public bool ShippedInImage => false;

    /// <summary>Unused: a store matches its station by number, never by distance.</summary>
    public double MatchRadiusM => 0;

    /// <summary>
    /// Asks each box in turn, and keeps one array of the stores found, each once (by <c>id</c>), with only
    /// <see cref="KeptFields"/>, as the locator answers. Any request that fails fails the run.
    /// </summary>
    public async Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var o = options.Value.CircleK;
        var stores = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < o.BoxList.Count; i++)
        {
            if (i > 0)
            {
                await pacer.PauseAsync(cancellationToken);
            }

            var body = await client.GetAsync(new Uri(o.BoxList[i].Fill(o.Url)), cancellationToken);
            foreach (var store in Stores(body))
            {
                if (HoursJson.Text(store, "id") is { } id && seen.Add(id))
                {
                    var kept = HoursJson.Keep(store, KeptFields);
                    if (store["address"] is JsonObject address)
                    {
                        kept["address"] = HoursJson.Keep(address, KeptAddress);
                    }

                    stores.Add(kept);
                }
            }
        }

        var raw = HoursJson.Bytes(stores);
        var version = HoursVersion.Of(raw);
        return version == cachedVersion ? new HoursFetch.NotModified() : new HoursFetch.Fetched(raw, version);
    }

    /// <summary>
    /// Québec's stores with their number and their week. A store whose number isn't one of Couche-Tard's Québec stores,
    /// or whose week can't be read for sure (no hours at all, a day missing), is left out with a warning.
    /// </summary>
    public HoursParse Parse(byte[] raw)
    {
        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in Stores(raw))
        {
            var address = store["address"] as JsonObject ?? [];
            if (!string.Equals(HoursJson.Text(address, "state"), "QC", StringComparison.OrdinalIgnoreCase)
                || HoursJson.Text(address, "country") is { } country && !string.Equals(country, "CA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = HoursJson.Text(store, "id");
            var what = $"Couche-Tard {id ?? "?"} ({HoursJson.Text(address, "street")}, {HoursJson.Text(address, "city")})";
            if (id is null || !seen.Add(id))
            {
                warnings.Add($"{what} has no id, or the same as another; left out.");
                continue;
            }

            if (StoreNumber(id) is not { } number)
            {
                warnings.Add($"{what} isn't numbered as Couche-Tard's Québec stores are ({StoreNumberBase + 1} and up); left out.");
                continue;
            }

            if (Rule(store, out var error) is not { } rule)
            {
                warnings.Add($"{what}: the store's hours can't be read for sure ({error}); left out.");
                continue;
            }

            var latitude = HoursJson.Number(store, "latitude");
            var longitude = HoursJson.Number(store, "longitude");
            var located = HoursJson.IsPoint(latitude, longitude);
            records.Add(new SourceRecord(
                id,
                BrandFamily.CoucheTard,
                located ? latitude : null,
                located ? longitude : null,
                HoursMatcher.HouseNumber(HoursJson.Text(address, "street")),
                StationKey.NormalizePostalCode(HoursJson.Text(address, "postalCode")) is { Length: > 0 } postcode ? postcode : null,
                rule,
                Link,
                CheckedAt: null,
                TemporarilyClosed: false)
            {
                StoreNumber = number,
            });
        }

        return new HoursParse(records, warnings);
    }

    /// <summary>The number in the feed's names: 140 for the store 6000140; null for an id that isn't a Québec store's.</summary>
    public static int? StoreNumber(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var site) && site is > StoreNumberBase and < StoreNumberBase + 1_000_000
            ? site - StoreNumberBase
            : null;

    /// <summary>
    /// The store's week: <c>24/7</c> when <c>alwaysOpen</c>, else each day's <c>open</c> and <c>close</c> ("05:00" to
    /// "23:00", "06:30" to "00:30" past midnight, "00:00" to "24:00" all day), every day given. The fuel's hours come first
    /// when the store gives them.
    /// </summary>
    private static string? Rule(JsonObject store, out string? error)
    {
        var hours = store["openingHoursFuel"] is JsonObject fuel && HasHours(fuel) ? fuel : store["openingHours"] as JsonObject;
        if (hours is null || !HasHours(hours))
        {
            error = "it gives none";
            return null;
        }

        if (HoursJson.IsTrue(hours, "alwaysOpen"))
        {
            error = null;
            return OpeningHours.AlwaysOpen.ToRule();
        }

        var week = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>();
        foreach (var (key, value) in hours)
        {
            if (key == "alwaysOpen")
            {
                continue;
            }

            if (!RuleNormalizer.TryDay(key, out var day))
            {
                error = $"the day \"{key}\" isn't understood";
                return null;
            }

            var times = value as JsonObject;
            var open = times is null ? null : HoursJson.Text(times, "open");
            var close = times is null ? null : HoursJson.Text(times, "close");
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

        return RuleNormalizer.Week(week, out error);
    }

    /// <summary>Says it's always open, or gives a day: <c>{"alwaysOpen": false}</c> alone gives nothing.</summary>
    private static bool HasHours(JsonObject hours) =>
        hours.Any(field => field.Key != "alwaysOpen" || field.Value?.GetValueKind() == JsonValueKind.True);

    /// <summary>The stores of an answer: a JSON array of them.</summary>
    private static IEnumerable<JsonObject> Stores(byte[] body) =>
        HoursJson.Parse(body, "Circle K's locator") is JsonArray array
            ? array.OfType<JsonObject>()
            : throw new HoursFormatException("Circle K's locator answered something else than its list of stores.");
}
