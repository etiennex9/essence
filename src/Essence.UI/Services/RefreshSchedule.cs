namespace Essence.UI.Services;

/// <summary>
/// When to fetch prices again while the map sits still. The feed publishes every 5 minutes and Essence.Ingest imports each
/// version a few seconds to two minutes later, a delay that stays about the same from one version to the next. So the next
/// import should land about 5 minutes after the one the data came from: ask a little after that, and more often while it's late.
/// </summary>
public static class RefreshSchedule
{
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(5);

    /// <summary>Past the expected import, so a slightly slower one is already in.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(15);

    /// <summary>After refreshes that found nothing new: the import is late, or ingest is down. The last one repeats.</summary>
    private static readonly TimeSpan[] LateDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

    /// <param name="dataAsOf">When the data shown was imported; null if the API didn't say.</param>
    /// <param name="lateAttempts">Refreshes in a row that found nothing new (or failed).</param>
    public static TimeSpan Next(DateTimeOffset? dataAsOf, DateTimeOffset now, int lateAttempts)
    {
        if (dataAsOf is not { } importedAt)
        {
            return PublishInterval;
        }

        var expected = importedAt + PublishInterval + Margin;
        if (lateAttempts > 0 || expected <= now)
        {
            return LateDelays[Math.Clamp(lateAttempts - 1, 0, LateDelays.Length - 1)];
        }

        // The clamp keeps a device clock that's off from the API's from waiting too little or too long.
        var delay = expected - now;
        return delay < MinDelay ? MinDelay
            : delay > PublishInterval + Margin ? PublishInterval + Margin
            : delay;
    }
}
