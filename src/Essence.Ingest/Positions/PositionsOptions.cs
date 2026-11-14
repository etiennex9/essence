namespace Essence.Ingest.Positions;

/// <summary>
/// The <c>Positions</c> configuration section: finding, once a day, the stations the Régie's feed may put in the wrong
/// place, for a person to review (<see cref="PositionsWorker"/>).
/// </summary>
public sealed class PositionsOptions
{
    public const string SectionName = "Positions";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When each day's run starts, in Québec time (America/Toronto): after the hours sources' night (01:00–05:00), so a
    /// night's downloads are in.
    /// </summary>
    public TimeSpan RunAt { get; set; } = new(5, 30, 0);

    /// <summary>
    /// Also runs a minute after each start, so a new image's corrections take the stations they fix off the list at once,
    /// and a first deployment has its list without waiting for the night.
    /// </summary>
    public bool RunOnStart { get; set; } = true;

    /// <summary>
    /// The address search (Photon, the geocoding container) the stations' addresses are looked up in, ending with
    /// <c>/</c>: <c>http://geocoding:2322/</c>. Empty for none: only OpenStreetMap's elements and the brands' locators are
    /// compared then.
    /// </summary>
    public string? GeocodingUrl { get; set; }

    /// <summary>How long a lookup may take: the runs are in the background, a few dozen lookups a day.</summary>
    public TimeSpan GeocodingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary><see cref="GeocodingUrl"/> as a URL; null when it's empty.</summary>
    public Uri? GeocodingUri =>
        string.IsNullOrWhiteSpace(GeocodingUrl) ? null : Uri.TryCreate(GeocodingUrl, UriKind.Absolute, out var url) ? url : null;

    /// <summary>What's wrong with the settings, or null.</summary>
    public string? Validate()
    {
        if (RunAt < TimeSpan.Zero || RunAt >= TimeSpan.FromDays(1))
        {
            return "Positions:RunAt must be a time of day, from 00:00 to 23:59.";
        }

        if (!string.IsNullOrWhiteSpace(GeocodingUrl)
            && (GeocodingUri is not { Scheme: "http" or "https" } url || !url.AbsolutePath.EndsWith('/')))
        {
            return "Positions:GeocodingUrl must be an absolute http(s) URL ending with '/', or empty for none.";
        }

        return GeocodingTimeout <= TimeSpan.Zero ? "Positions:GeocodingTimeout must be positive." : null;
    }
}
