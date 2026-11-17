namespace Essence.Ingest.Hours;

/// <summary>The <c>Hours</c> configuration section: fetching stations' opening hours from their brands, once a week.</summary>
public sealed class HoursOptions
{
    public const string SectionName = "Hours";

    /// <summary>The retries after a failed or rejected run, when <see cref="Backoff"/> isn't set: then every 6 hours until one succeeds.</summary>
    public static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    public bool Enabled { get; set; } = true;

    /// <summary>Where each source's last good download is kept (<c>&lt;source&gt;/latest.raw</c>), to match it again without the network.</summary>
    public string CachePath { get; set; } = "/data/hours";

    /// <summary>Sent with every request. Overpass refuses generic ones; a contact URL can be added in parentheses.</summary>
    public string UserAgent { get; set; } = "Essence-Ingest/1.0 (station hours)";

    /// <summary>How often each source is fetched: at least a day, since each run is drawn in a night.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The night, in Québec time (America/Toronto), when a source's weekly run is drawn: from this time of day…</summary>
    public TimeSpan NightStart { get; set; } = TimeSpan.FromHours(1);

    /// <summary>… to this one.</summary>
    public TimeSpan NightEnd { get; set; } = TimeSpan.FromHours(5);

    /// <summary>
    /// Past this without a success, a failing run's log says the source's hours are stale. Only the log: the API has its
    /// own threshold (<c>Health:HoursStaleAfterDays</c>), and the two are best kept together.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(21);

    /// <summary>A run that matches fewer stations than this share of the last good one's is rejected, and the last good rows stay.</summary>
    public double MinShareOfLastGood { get; set; } = 0.8;

    /// <summary>How long after a failed or rejected run the source is tried again: the first failure, the second… the last one repeats.</summary>
    public TimeSpan[] Backoff { get; set; } = [];

    /// <summary>How often the sources' last downloads are matched again, when the feed's stations changed.</summary>
    public TimeSpan RematchEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Timeout of a single HTTP attempt.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retries of a request within a run, after a transient failure. None by default: a failed run is tried again later
    /// (<see cref="Backoff"/>), and a run then makes no more requests than it needs.
    /// </summary>
    public int RetryCount { get; set; }

    public HoursSourceOptions Curated { get; set; } = new();
    public CostcoOptions Costco { get; set; } = new();

    /// <summary><see cref="Backoff"/>, or <see cref="DefaultBackoff"/> when it's empty.</summary>
    public IReadOnlyList<TimeSpan> BackoffSteps => Backoff.Length > 0 ? Backoff : DefaultBackoff;

    /// <summary>What's wrong with the settings, or null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(CachePath))
        {
            return "Hours:CachePath is required.";
        }

        using var probe = new HttpRequestMessage();
        if (string.IsNullOrWhiteSpace(UserAgent) || !probe.Headers.UserAgent.TryParseAdd(UserAgent))
        {
            return "Hours:UserAgent must be a valid User-Agent, e.g. \"Essence-Ingest/1.0 (station hours)\".";
        }

        if (Every < TimeSpan.FromDays(1))
        {
            return "Hours:Every must be at least a day: each run is drawn in a night.";
        }

        if (StaleAfter <= TimeSpan.Zero || RematchEvery <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero)
        {
            return "Hours:StaleAfter, RematchEvery and RequestTimeout must be positive.";
        }

        if (NightStart < TimeSpan.Zero || NightEnd > TimeSpan.FromHours(24) || NightStart >= NightEnd)
        {
            return "Hours:NightStart must come before Hours:NightEnd, within a day.";
        }

        if (MinShareOfLastGood is < 0 or > 1)
        {
            return "Hours:MinShareOfLastGood must be between 0 and 1.";
        }

        if (Backoff.Any(b => b <= TimeSpan.Zero) || RetryCount < 0)
        {
            return "Hours:Backoff must be positive and Hours:RetryCount not negative.";
        }

        return Costco.Validate();
    }
}

/// <summary>A source's settings: <c>Hours:&lt;Source&gt;</c>.</summary>
public class HoursSourceOptions
{
    public bool Enabled { get; set; } = true;
}

/// <summary><c>Hours:Costco</c>: Costco's warehouse locator.</summary>
public sealed class CostcoOptions : HoursSourceOptions
{
    /// <summary>
    /// The locator's search for warehouses with a gas bar, with <c>{lat}</c> and <c>{lon}</c> where the point goes. It
    /// answers the 50 nearest, whose details include the gas bar's hours.
    /// </summary>
    public string Url { get; set; } =
        "https://www.costco.ca/AjaxWarehouseBrowseLookupView?langId=-24&storeId=10302&numOfWarehouses=50&hasGas=true"
        + "&hasTires=false&hasFood=false&hasHearing=false&hasPharmacy=false&hasOptical=false&hasBusiness=false&hasPhotoCenter="
        + "&tiresCheckout=0&isTransferWarehouse=false&populateWarehouseDetails=true&warehousePickupCheckout=false"
        + "&latitude={lat}&longitude={lon}&countryCode=CA";

    internal string? Validate() =>
        Url.Contains("{lat}", StringComparison.Ordinal) && Url.Contains("{lon}", StringComparison.Ordinal)
        && Uri.TryCreate(Url.Replace("{lat}", "0").Replace("{lon}", "0"), UriKind.Absolute, out var url)
        && url.Scheme is "https" or "http"
            ? null
            : "Hours:Costco:Url must be an absolute http(s) URL with {lat} and {lon} in it.";
}
