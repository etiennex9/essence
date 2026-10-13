namespace Essence.Ingest.Feed;

/// <summary>The <c>Feed</c> configuration section: where to fetch and how to schedule polls.</summary>
public sealed class FeedOptions
{
    public const string SectionName = "Feed";

    public Uri Url { get; set; } = new("https://regieessencequebec.ca/stations.geojson.gz");

    /// <summary>How often the Régie generates a new file (at :00:01, :05:01, … UTC).</summary>
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the polling window opens when no import history exists yet.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(40);

    public TimeSpan MinDelay { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>The learned delay is this percentile of recent generated→fetched delays.</summary>
    public int DelayPercentile { get; set; } = 20;

    /// <summary>How many recent delays the percentile is computed from.</summary>
    public int DelayHistorySize { get; set; } = 50;

    /// <summary>Poll interval while the window is open.</summary>
    public TimeSpan FastPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the window stays open before falling back to <see cref="SlowPollInterval"/>.</summary>
    public TimeSpan FastPollDuration { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan SlowPollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Each poll time moves randomly by up to ± this much.</summary>
    public TimeSpan Jitter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout of a single HTTP attempt.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after a transient HTTP failure.</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>After this many failed imports of the same file (by SHA-256), it's skipped. It stays in the raw archive.</summary>
    public int MaxImportAttempts { get; set; } = 3;
}
