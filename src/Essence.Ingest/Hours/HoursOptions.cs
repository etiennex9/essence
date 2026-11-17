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

    /// <summary>
    /// The shortest pause between two requests to the same source in a run (Couche-Tard's boxes, Shell's lists and
    /// details), drawn at random up to <see cref="MaxPause"/> (<see cref="HoursPacer"/>).
    /// </summary>
    public TimeSpan MinPause { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest pause between two requests to the same source.</summary>
    public TimeSpan MaxPause { get; set; } = TimeSpan.FromSeconds(3);

    public HoursSourceOptions Curated { get; set; } = new();
    public CostcoOptions Costco { get; set; } = new();
    public UltramarOptions Ultramar { get; set; } = new();
    public ShellOptions Shell { get; set; } = new();
    public CircleKOptions CircleK { get; set; } = new();
    public OsmOptions Osm { get; set; } = new();

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

        if (MinPause < TimeSpan.Zero || MaxPause < MinPause)
        {
            return "Hours:MinPause must not be negative, nor more than Hours:MaxPause.";
        }

        return Costco.Validate() ?? Ultramar.Validate() ?? Shell.Validate() ?? CircleK.Validate() ?? Osm.Validate();
    }

    /// <summary>An absolute http(s) URL once <paramref name="placeholders"/> are filled in, each of them in it.</summary>
    internal static bool IsUrl(string? template, params string[] placeholders)
    {
        if (string.IsNullOrWhiteSpace(template) || placeholders.Any(p => !template.Contains(p, StringComparison.Ordinal)))
        {
            return false;
        }

        var filled = placeholders.Aggregate(template, (url, placeholder) => url.Replace(placeholder, "0", StringComparison.Ordinal));
        return Uri.TryCreate(filled, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
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
        HoursOptions.IsUrl(Url, "{lat}", "{lon}") ? null : "Hours:Costco:Url must be an absolute http(s) URL with {lat} and {lon} in it.";
}

/// <summary>An area to ask a locator about, in degrees.</summary>
public sealed class GeoBox
{
    public double North { get; set; }
    public double West { get; set; }
    public double South { get; set; }
    public double East { get; set; }

    public GeoBox()
    {
    }

    public GeoBox(double north, double west, double south, double east) => (North, West, South, East) = (north, west, south, east);

    /// <summary>The URL with <c>{north}</c>, <c>{west}</c>, <c>{south}</c> and <c>{east}</c> filled in.</summary>
    public string Fill(string template) => template
        .Replace("{north}", Degrees(North), StringComparison.Ordinal)
        .Replace("{west}", Degrees(West), StringComparison.Ordinal)
        .Replace("{south}", Degrees(South), StringComparison.Ordinal)
        .Replace("{east}", Degrees(East), StringComparison.Ordinal);

    internal bool IsValid => South is >= -90 and <= 90 && North is >= -90 and <= 90 && South < North
        && West is >= -180 and <= 180 && East is >= -180 and <= 180 && West < East;

    internal static readonly string[] Placeholders = ["{north}", "{west}", "{south}", "{east}"];

    private static string Degrees(double value) => value.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary><c>Hours:Ultramar</c>: Ultramar's station locator (journie.ca), one request for all of Canada's stations.</summary>
public sealed class UltramarOptions : HoursSourceOptions
{
    /// <summary>Every station within 100,000 km of Québec City, a page of up to 1,000: about 620, 364 of them in Québec.</summary>
    public string Url { get; set; } =
        "https://journie.ca/ultramar/api/locations/nearest?country=CA&pageSize=1000&lat=46.8&lon=-71.3&range=100000000&brand=Ultramar";

    internal string? Validate() =>
        HoursOptions.IsUrl(Url) ? null : "Hours:Ultramar:Url must be an absolute http(s) URL.";
}

/// <summary><c>Hours:CircleK</c>: Circle K's store locator, which has Couche-Tard's Québec stores.</summary>
public sealed class CircleKOptions : HoursSourceOptions
{
    /// <summary>The boxes asked when <see cref="Boxes"/> isn't set: Québec's west and east, each answering up to 2,000 stores.</summary>
    public static readonly GeoBox[] DefaultBoxes = [new(62.6, -79.8, 44.9, -71.5), new(62.6, -71.5, 44.9, -57.0)];

    /// <summary>The search of a box's stores, with <c>{north}</c>, <c>{west}</c>, <c>{south}</c> and <c>{east}</c> where its edges go.</summary>
    public string Url { get; set; } =
        "https://api.circlek.com/us/ngrp-store-locator/v1/stations?topLeftLatitude={north}&topLeftLongitude={west}"
        + "&bottomRightLatitude={south}&bottomRightLongitude={east}&maxResults=2000&country=CA&brand=CIRCLEK,COUCHE_TARD";

    /// <summary>The boxes asked, one request each; <see cref="DefaultBoxes"/> when empty.</summary>
    public GeoBox[] Boxes { get; set; } = [];

    public IReadOnlyList<GeoBox> BoxList => Boxes.Length > 0 ? Boxes : DefaultBoxes;

    internal string? Validate() =>
        !HoursOptions.IsUrl(Url, GeoBox.Placeholders)
            ? "Hours:CircleK:Url must be an absolute http(s) URL with {north}, {west}, {south} and {east} in it."
            : BoxList.Any(b => !b.IsValid)
                ? "Hours:CircleK:Boxes must be boxes in degrees, South below North and West left of East."
                : null;
}

/// <summary><c>Hours:Shell</c>: Shell's station locator. Its lists give the stations, a request per station their hours.</summary>
public sealed class ShellOptions : HoursSourceOptions
{
    /// <summary>
    /// The stations in a box, or the clusters of them when there are many, with <c>{north}</c>, <c>{west}</c>,
    /// <c>{south}</c> and <c>{east}</c> where its edges go.
    /// </summary>
    public string ListUrl { get; set; } =
        "https://shellgsllocator.geoapp.me/api/v2/locations/within_bounds?sw%5B%5D={south}&sw%5B%5D={west}&ne%5B%5D={north}&ne%5B%5D={east}";

    /// <summary>A station's details, its hours among them, with <c>{id}</c> where its id goes.</summary>
    public string DetailUrl { get; set; } = "https://shellgsllocator.geoapp.me/api/v2/locations/{id}";

    /// <summary>The box listed: Québec, with a little of its neighbours, whose stations are left out.</summary>
    public GeoBox Box { get; set; } = new(62.6, -79.8, 44.9, -57.0);

    /// <summary>Most list requests a run makes, splitting clusters down to stations: about 22 today.</summary>
    public int MaxListRequests { get; set; } = 100;

    internal string? Validate() =>
        !HoursOptions.IsUrl(ListUrl, GeoBox.Placeholders)
            ? "Hours:Shell:ListUrl must be an absolute http(s) URL with {north}, {west}, {south} and {east} in it."
            : !HoursOptions.IsUrl(DetailUrl, "{id}")
                ? "Hours:Shell:DetailUrl must be an absolute http(s) URL with {id} in it."
                : !Box.IsValid
                    ? "Hours:Shell:Box must be a box in degrees, South below North and West left of East."
                    : MaxListRequests < 1
                        ? "Hours:Shell:MaxListRequests must be at least 1."
                        : null;
}

/// <summary><c>Hours:Osm</c>: OpenStreetMap's fuel stations in Québec, from an Overpass server.</summary>
public sealed class OsmOptions : HoursSourceOptions
{
    /// <summary>The servers asked when <see cref="Endpoints"/> isn't set, in order: the next one when one fails.</summary>
    public static readonly string[] DefaultEndpoints =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
    ];

    /// <summary>The servers asked, in order; <see cref="DefaultEndpoints"/> when empty.</summary>
    public string[] Endpoints { get; set; } = [];

    /// <summary>
    /// Every fuel station in Québec, with its tags and its point (a way's or a relation's centre), never who mapped it:
    /// about 2,860 elements, a minute's work for the server.
    /// </summary>
    public string Query { get; set; } =
        "[out:json][timeout:180];area[\"ISO3166-2\"=\"CA-QC\"]->.qc;nwr[\"amenity\"=\"fuel\"](area.qc);out tags center;";

    /// <summary>How long a server may take to answer, longer than the query's own timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(4);

    public IReadOnlyList<string> EndpointList => Endpoints.Length > 0 ? Endpoints : DefaultEndpoints;

    internal string? Validate() =>
        EndpointList.Any(e => !HoursOptions.IsUrl(e))
            ? "Hours:Osm:Endpoints must be absolute http(s) URLs."
            : string.IsNullOrWhiteSpace(Query)
                ? "Hours:Osm:Query is required."
                : RequestTimeout <= TimeSpan.Zero
                    ? "Hours:Osm:RequestTimeout must be positive."
                    : null;
}
