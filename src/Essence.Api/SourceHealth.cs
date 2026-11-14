using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>What <c>/health/sources</c> holds a source to.</summary>
/// <param name="MaxImportAge">The feed: the oldest its last successful import may be (<c>Health:MaxImportAgeMinutes</c>, 15), as for <c>/health</c>.</param>
/// <param name="HoursStaleAfter">An hours source: the longest it may go without a success (<c>Health:HoursStaleAfterDays</c>, 21), as for <c>/health</c>.</param>
/// <param name="AlertAfterFailures">An hours source: how many failed or rejected fetches in a row fail it (<c>Health:HoursAlertAfterFailures</c>, 3).</param>
public sealed record SourceHealthSettings(TimeSpan MaxImportAge, TimeSpan HoursStaleAfter, int AlertAfterFailures)
{
    public const int DefaultAlertAfterFailures = 3;

    public static SourceHealthSettings Default { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromDays(21), DefaultAlertAfterFailures);

    /// <summary>The settings, read the way <c>/health</c>'s checks read theirs; fewer than 1 failure counts as 1.</summary>
    public static SourceHealthSettings From(IConfiguration configuration) => new(
        IngestHealthCheck.MaxImportAge(configuration),
        HoursHealthCheck.StaleAfter(configuration),
        Math.Max(1, configuration.GetValue("Health:HoursAlertAfterFailures", DefaultAlertAfterFailures)));
}

/// <summary>What the feed's imports say, for <c>/health/sources</c>.</summary>
/// <param name="LastSuccessAt">When the latest successful import was fetched, as <see cref="IngestHealthCheck"/> reads it; null when none was.</param>
/// <param name="LastAttemptAt">When the latest import was fetched, whatever came of it.</param>
/// <param name="FailuresSinceSuccess">The failed imports since then (each of a new file that couldn't be written).</param>
/// <param name="LastError">The latest of those failures' stored error; null when there's none.</param>
public sealed record FeedRuns(DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, int FailuresSinceSuccess, string? LastError);

/// <summary>
/// <c>/health/sources</c>: whether each source the ingest reads is doing fine, for a monitor that alerts on a 503 (README →
/// Alerts). The app decides when something's wrong, the monitor only sends it.
/// <list type="bullet">
/// <item>The Régie's feed (<c>feed</c>) fails when its last successful import is older than <c>Health:MaxImportAgeMinutes</c>
/// (15, about 3 of its versions in a row): <see cref="IngestHealthCheck"/>'s own rule.</item>
/// <item>An hours source fails after <c>Health:HoursAlertAfterFailures</c> (3) failed or rejected fetches in a row (about
/// 1¼ h after its weekly run, with the retries after 15 min, then 1 h), whether it ever succeeded or not, or when it has
/// had no success in <c>Health:HoursStaleAfterDays</c> (21) though it ran before: it stopped running. Its runs are read
/// as <see cref="HoursHistory"/> reads them: <see cref="HoursImportStatus.Unchanged"/> is a success, which clears it; a
/// <see cref="HoursImportStatus.Rematched"/> run fetched nothing, so it neither counts as a failure nor clears one.
/// Sources turned off aren't listed.</item>
/// </list>
/// Apart from <c>/health</c>, so that routing or geocoding building their data never raises an alert, and from
/// <c>/alive</c>, which the containers' healthchecks ask: a failing source never restarts anything.
/// </summary>
public static class SourceHealth
{
    /// <summary>The Régie's feed, among the sources.</summary>
    public const string Feed = "feed";

    /// <summary>The report's status when every source is fine (200).</summary>
    public const string OkStatus = "ok";

    /// <summary>The report's status when one isn't, or the runs can't be read (503).</summary>
    public const string FailingStatus = "failing";

    /// <summary>Why a source isn't fine: an hours source's fetches failed or were rejected too many times in a row.</summary>
    public const string FailingReason = "failing";

    /// <summary>An hours source without a success for too long, though it ran before: it stopped running.</summary>
    public const string StaleReason = "stale";

    /// <summary>The feed without a successful import ever, or an hours source that failed too many times without one.</summary>
    public const string NeverSucceededReason = "never succeeded";

    /// <summary>The feed without a successful import for too long.</summary>
    public const string FeedStaleReason = "feed stale";

    /// <summary>Why the whole report is failing: the runs couldn't be read.</summary>
    public const string DatabaseUnreachableReason = "database unreachable";

    /// <summary>The logger category: a database that can't be read is logged there, never in the answer.</summary>
    public const string LogCategory = "Essence.Api.SourceHealth";

    /// <summary>Every source's state now, read from <c>imports</c> and <c>hours_imports</c>.</summary>
    /// <remarks>Throws what the database throws: the endpoint answers <see cref="Unreachable"/> then.</remarks>
    public static async Task<SourceHealthReport> LoadAsync(
        EssenceDbContext db, DateTimeOffset now, SourceHealthSettings settings, CancellationToken cancellationToken)
    {
        var feed = await LoadFeedAsync(db, cancellationToken);
        var hours = await HoursHistory.LoadAsync(db, cancellationToken);
        return Report(now, settings, feed, hours);
    }

    /// <summary>The feed's imports: the last success as <see cref="IngestHealthCheck"/> reads it, and the failures since.</summary>
    public static async Task<FeedRuns> LoadFeedAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        var lastSuccessAt = await IngestHealthCheck.LastImportAtAsync(db, cancellationToken);
        var failures = db.Imports
            .Where(i => i.Status == ImportStatus.Failed && (lastSuccessAt == null || i.FetchedAt > lastSuccessAt));
        var count = await failures.CountAsync(cancellationToken);
        var latest = count == 0
            ? null
            : await failures
                .OrderByDescending(i => i.FetchedAt)
                .ThenByDescending(i => i.Id)
                .Select(i => new { i.FetchedAt, i.Error })
                .FirstOrDefaultAsync(cancellationToken);
        return new FeedRuns(lastSuccessAt, latest?.FetchedAt ?? lastSuccessAt, count, latest?.Error);
    }

    /// <summary>
    /// The report: the feed first, then each hours source by name, leaving out those turned off
    /// (<see cref="HoursSourceHistory.Disabled"/>); <c>failing</c> as soon as one isn't fine.
    /// </summary>
    public static SourceHealthReport Report(
        DateTimeOffset now, SourceHealthSettings settings, FeedRuns feed, IEnumerable<HoursSourceHistory> hours)
    {
        IngestSourceHealth[] sources =
        [
            ForFeed(feed, now, settings),
            .. hours.Where(h => !h.Disabled).OrderBy(h => h.Source, StringComparer.Ordinal).Select(h => ForHours(h, now, settings)),
        ];
        return new SourceHealthReport(sources.All(s => s.Ok) ? OkStatus : FailingStatus, now, sources);
    }

    /// <summary>The answer when the runs can't be read: failing, with no source and no detail of the error.</summary>
    public static SourceHealthReport Unreachable(DateTimeOffset now) => new(FailingStatus, now, [], DatabaseUnreachableReason);

    /// <summary>The feed: fine while its last successful import is recent, by <see cref="IngestHealthCheck.IsRecent"/>.</summary>
    public static IngestSourceHealth ForFeed(FeedRuns feed, DateTimeOffset now, SourceHealthSettings settings)
    {
        var reason = feed.LastSuccessAt switch
        {
            null => NeverSucceededReason,
            { } at when !IngestHealthCheck.IsRecent(at, now, settings.MaxImportAge) => FeedStaleReason,
            _ => null,
        };
        return new IngestSourceHealth(
            Feed,
            reason is null,
            reason,
            feed.FailuresSinceSuccess,
            HoursSources.FirstLine(feed.LastError),
            feed.LastSuccessAt,
            feed.LastAttemptAt);
    }

    /// <summary>
    /// An hours source: <c>failing</c> after <see cref="SourceHealthSettings.AlertAfterFailures"/> failed or rejected
    /// fetches in a row (<c>never succeeded</c> when it never had a success), else <c>stale</c> past
    /// <see cref="SourceHealthSettings.HoursStaleAfter"/> without one (for a source that never succeeded, since it was last
    /// tried: it stopped running). Fewer failures than that, with a success not too old (or a recent try), are fine: a retry
    /// is on its way.
    /// </summary>
    public static IngestSourceHealth ForHours(HoursSourceHistory history, DateTimeOffset now, SourceHealthSettings settings)
    {
        var failing = history.FailuresSinceSuccess >= settings.AlertAfterFailures;
        var stale = (history.LastSuccess ?? history.LastAttempt) is { } latest
            && HoursHealthCheck.IsStale(latest.FinishedAt, now, settings.HoursStaleAfter);
        var reason = (history.LastSuccess, failing, stale) switch
        {
            (null, true, _) => NeverSucceededReason,
            (_, true, _) => FailingReason,
            (_, _, true) => StaleReason,
            _ => null,
        };
        return new IngestSourceHealth(
            history.Source,
            reason is null,
            reason,
            history.FailuresSinceSuccess,
            HoursSources.LastError(history),
            history.LastSuccess?.FinishedAt,
            history.LastAttempt?.FinishedAt);
    }
}
