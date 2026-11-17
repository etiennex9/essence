using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>What the schedule needs of a source's runs.</summary>
/// <param name="LastSuccessAt">When its last success finished; null when it never succeeded.</param>
/// <param name="LastAttemptAt">When its last fetch finished, whatever came of it.</param>
/// <param name="FailuresSinceSuccess">Failed or rejected fetches in a row since then.</param>
public sealed record SourceRuns(DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, int FailuresSinceSuccess);

/// <summary>
/// When each source is fetched: once every <see cref="HoursOptions.Every"/> (7 days), at a minute drawn at random in that
/// night's window (01:00–05:00 in Québec), drawn again each week, so the brands' sites see no fixed pattern and a quiet
/// hour. A source that never succeeded runs soon after the start, a minute after the one before it, and a failed or
/// rejected run is tried again after 15 minutes, then 1 hour, then every 6 hours until one succeeds. Pure: the clock
/// comes from <see cref="TimeProvider"/> and the draws from the <see cref="Random"/> given.
/// </summary>
public sealed class HoursSchedule(IOptions<HoursOptions> options, TimeProvider timeProvider, Random random)
{
    /// <summary>Between the first runs of the sources after a start.</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMinutes(1);

    /// <summary>When the source runs next: never before <paramref name="startedAt"/> + <paramref name="index"/> × <see cref="Stagger"/>.</summary>
    /// <param name="startedAt">When the worker started.</param>
    /// <param name="index">The source's place among the sources.</param>
    public DateTimeOffset NextRun(SourceRuns runs, DateTimeOffset startedAt, int index)
    {
        var o = options.Value;
        var due = runs switch
        {
            { FailuresSinceSuccess: > 0, LastAttemptAt: { } failedAt } => failedAt + RetryAfter(runs.FailuresSinceSuccess),
            // Every is a day or more (HoursOptions.Validate), so that night is always after the success.
            { LastSuccessAt: { } succeededAt } => InNight(succeededAt + o.Every),
            _ => timeProvider.GetUtcNow(),
        };
        var earliest = startedAt + Stagger * index;
        return due > earliest ? due : earliest;
    }

    /// <summary>How long after its <paramref name="failures"/>th failure in a row a source is tried again.</summary>
    public TimeSpan RetryAfter(int failures)
    {
        var steps = options.Value.BackoffSteps;
        return steps[Math.Clamp(failures - 1, 0, steps.Count - 1)];
    }

    /// <summary>A minute drawn at random in the night window of <paramref name="at"/>'s day, in Québec time.</summary>
    private DateTimeOffset InNight(DateTimeOffset at)
    {
        var o = options.Value;
        var zone = QuebecTime.Eastern;
        var day = TimeZoneInfo.ConvertTime(at, zone).Date;

        // Instants, not clock times: on the night daylight time starts the window is an hour shorter, and an hour longer
        // the night it ends, and no time drawn is one the clock skips or repeats.
        var start = Instant(day + o.NightStart, zone);
        var end = Instant(day + o.NightEnd, zone);
        var minutes = Math.Max(1, (int)(end - start).TotalMinutes);
        return start + TimeSpan.FromMinutes(random.Next(minutes));
    }

    /// <summary>A local time's instant: one the clock skips is read with the offset from before, one it repeats is the first.</summary>
    internal static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(local)
            ? zone.GetUtcOffset(local.AddHours(-3))
            : zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).Max()
                : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
