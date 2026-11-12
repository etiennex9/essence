using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Essence.Api;

/// <summary>
/// How the hours sources' runs are going, from <c>hours_imports</c> (<c>/hours/sources</c>, the <c>hours</c> health check):
/// those that have run and aren't turned off.
/// </summary>
public static class HoursSources
{
    public static async Task<IReadOnlyList<HoursSourceStatus>> LoadAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        var histories = await HoursHistory.LoadAsync(db, cancellationToken);
        var stations = await db.StationHours
            .GroupBy(h => h.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Source, g => g.Count, cancellationToken);
        return histories.Select(h => ToStatus(h, stations.GetValueOrDefault(h.Source))).ToList();
    }

    public static HoursSourceStatus ToStatus(HoursSourceHistory history, int stations)
    {
        var attempt = history.LastAttempt;
        return new HoursSourceStatus(
            history.Source,
            history.LastSuccess?.FinishedAt,
            attempt?.FinishedAt,
            attempt is null ? null : Name(attempt.Status),
            attempt is { Status: HoursImportStatus.Failed or HoursImportStatus.Rejected } ? FirstLine(attempt.Error) : null,
            history.FailuresSinceSuccess,
            stations,
            history.LastGood?.SourceVersion);
    }

    /// <summary>"succeeded", as stored.</summary>
    private static string Name(HoursImportStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>A failure's stored error includes a stack trace; the public API only shows the exception type and message.</summary>
    private static string? FirstLine(string? error) => error?.Split('\n', 2)[0].TrimEnd('\r');
}

/// <summary>
/// Degraded when a source that has run has no success within <c>Health:HoursStaleAfterDays</c> (21), or none ever: its
/// hours still show, but are stale. A source that has never run doesn't count, nor does one turned off (its rows went
/// with it, <see cref="HoursImportStatus.Disabled"/>), nor do hours themselves: a station without them is only "hours
/// unknown".
/// </summary>
public sealed class HoursHealthCheck(EssenceDbContext db, TimeProvider timeProvider, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var staleAfter = TimeSpan.FromDays(configuration.GetValue("Health:HoursStaleAfterDays", 21));
        IReadOnlyList<HoursSourceStatus> sources;
        try
        {
            sources = await HoursSources.LoadAsync(db, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The hours imports can't be read.", ex);
        }

        return Evaluate(sources, timeProvider.GetUtcNow(), staleAfter, context.Registration.FailureStatus);
    }

    /// <summary>The check's result for these sources: its data gives each one's last success (or <c>never</c>).</summary>
    public static HealthCheckResult Evaluate(
        IReadOnlyList<HoursSourceStatus> sources, DateTimeOffset now, TimeSpan staleAfter, HealthStatus failureStatus)
    {
        var data = new Dictionary<string, object> { ["staleAfterDays"] = staleAfter.TotalDays };
        foreach (var source in sources)
        {
            data[source.Source] = source.LastSuccessAt is { } at ? at : "never";
        }

        if (sources.Count == 0)
        {
            return HealthCheckResult.Healthy("No hours source has run yet.", data);
        }

        // A source that ran but never succeeded has no hours to be stale: it's named apart, so the description says which.
        var never = sources.Where(s => s.LastSuccessAt is null).Select(s => s.Source).ToList();
        var stale = sources.Where(s => s.LastSuccessAt is { } at && now - at > staleAfter).Select(s => s.Source).ToList();
        if (never.Count == 0 && stale.Count == 0)
        {
            return HealthCheckResult.Healthy($"Every hours source succeeded within {staleAfter.TotalDays:0} days.", data);
        }

        var problems = new List<string>(2);
        if (stale.Count > 0)
        {
            problems.Add($"No success within {staleAfter.TotalDays:0} days for the hours from {string.Join(", ", stale)}: they're stale.");
        }

        if (never.Count > 0)
        {
            problems.Add($"No success yet for the hours from {string.Join(", ", never)}.");
        }

        return new HealthCheckResult(failureStatus, string.Join(" ", problems), data: data);
    }
}
