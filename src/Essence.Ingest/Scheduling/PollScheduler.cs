using Essence.Ingest.Feed;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Scheduling;

public enum PollPhase
{
    /// <summary>Nothing known yet, or a newer file is probably already available: poll now.</summary>
    Immediate,

    /// <summary>The latest file is imported; sleep until the next window opens.</summary>
    AwaitingWindow,

    /// <summary>Inside the window: poll every <see cref="FeedOptions.FastPollInterval"/>.</summary>
    Window,

    /// <summary>A window closed without a new file: poll every <see cref="FeedOptions.SlowPollInterval"/> until the next one opens.</summary>
    Fallback,
}

public readonly record struct PollPlan(DateTimeOffset At, PollPhase Phase);

/// <summary>
/// Decides when to poll, aligned to the feed's <c>generated_at</c> rather than a fixed interval.
/// The next file is expected at last generated_at + <see cref="FeedOptions.PublishInterval"/>; it reaches the CDN a little later,
/// so the polling window opens after a delay learned from recent imports. Pure logic: time comes from <see cref="TimeProvider"/>.
/// </summary>
public sealed class PollScheduler(IOptions<FeedOptions> options, TimeProvider timeProvider)
{
    private readonly FeedOptions _options = options.Value;
    private readonly Queue<double> _delays = new();
    private DateTimeOffset? _lastGeneratedAt;
    private DateTimeOffset? _lastPollAt;

    public DateTimeOffset? LastGeneratedAt => _lastGeneratedAt;

    /// <summary>
    /// The <see cref="FeedOptions.DelayPercentile"/>th percentile (nearest rank) of recent delays,
    /// clamped to [<see cref="FeedOptions.MinDelay"/>, <see cref="FeedOptions.MaxDelay"/>].
    /// </summary>
    public TimeSpan LearnedDelay
    {
        get
        {
            if (_delays.Count == 0)
            {
                return _options.InitialDelay;
            }

            var sorted = _delays.Order().ToArray();
            var rank = (int)Math.Ceiling(_options.DelayPercentile / 100.0 * sorted.Length);
            var delay = TimeSpan.FromSeconds(sorted[Math.Clamp(rank, 1, sorted.Length) - 1]);
            return delay < _options.MinDelay ? _options.MinDelay
                : delay > _options.MaxDelay ? _options.MaxDelay
                : delay;
        }
    }

    /// <summary>Loads the state persisted in <c>imports</c>.</summary>
    /// <param name="recentDelaysOldestFirst">Delays (seconds) of recent successful imports.</param>
    public void Initialize(DateTimeOffset? lastGeneratedAt, IEnumerable<double> recentDelaysOldestFirst)
    {
        _lastGeneratedAt = lastGeneratedAt;
        _lastPollAt = null;
        _delays.Clear();
        foreach (var delay in recentDelaysOldestFirst)
        {
            AddDelay(delay);
        }
    }

    /// <summary>Records that a poll started at <paramref name="at"/>.</summary>
    public void OnPolled(DateTimeOffset at) => _lastPollAt = at;

    /// <summary>Records a newly imported (or deliberately skipped) version of the feed.</summary>
    /// <param name="delaySeconds">generated→fetched delay to learn from, or null.</param>
    public void OnNewVersion(DateTimeOffset generatedAt, double? delaySeconds)
    {
        if (_lastGeneratedAt is null || generatedAt > _lastGeneratedAt)
        {
            _lastGeneratedAt = generatedAt;
        }

        if (delaySeconds is { } delay)
        {
            AddDelay(delay);
        }
    }

    public PollPlan NextPoll()
    {
        var now = timeProvider.GetUtcNow();
        if (_lastGeneratedAt is not { } lastGeneratedAt)
        {
            return _lastPollAt is { } polled
                ? Plan(polled + _options.SlowPollInterval, PollPhase.Fallback, now)
                : new PollPlan(now, PollPhase.Immediate);
        }

        // Window k opens at lastGeneratedAt + (k + 1) × interval + delay and stays open FastPollDuration.
        // Roll forward to the first window that hasn't closed yet, which realigns to the 5-min grid after missed slots.
        var interval = _options.PublishInterval;
        var firstOpensAt = lastGeneratedAt + interval + LearnedDelay;
        var firstClosesAt = firstOpensAt + _options.FastPollDuration;
        var windowIndex = now < firstClosesAt ? 0 : (now - firstClosesAt).Ticks / interval.Ticks + 1;
        var opensAt = firstOpensAt + TimeSpan.FromTicks(interval.Ticks * windowIndex);

        if (now >= opensAt)
        {
            return _lastPollAt is { } polled
                ? Plan(polled + _options.FastPollInterval, PollPhase.Window, now)
                : new PollPlan(now, PollPhase.Immediate);
        }

        if (windowIndex == 0)
        {
            return Plan(opensAt, PollPhase.AwaitingWindow, now);
        }

        // At least one window passed without a new file (or we just started after downtime).
        if (_lastPollAt is not { } lastPoll)
        {
            return new PollPlan(now, PollPhase.Immediate);
        }

        var slowPollAt = lastPoll + _options.SlowPollInterval;
        return Plan(slowPollAt < opensAt ? slowPollAt : opensAt, PollPhase.Fallback, now);
    }

    private PollPlan Plan(DateTimeOffset at, PollPhase phase, DateTimeOffset now)
    {
        if (_options.Jitter > TimeSpan.Zero)
        {
            at += TimeSpan.FromTicks((long)((Random.Shared.NextDouble() * 2 - 1) * _options.Jitter.Ticks));
        }

        return new PollPlan(at < now ? now : at, phase);
    }

    private void AddDelay(double seconds)
    {
        // A delay of a whole interval or more means we started late (downtime), not that the CDN was slow.
        if (seconds < 0 || seconds >= _options.PublishInterval.TotalSeconds)
        {
            return;
        }

        _delays.Enqueue(seconds);
        while (_delays.Count > _options.DelayHistorySize)
        {
            _delays.Dequeue();
        }
    }
}
