using Essence.Ingest.Feed;
using Essence.Ingest.Scheduling;
using Essence.Ingest.Storage;
using Microsoft.Extensions.Options;

namespace Essence.Ingest;

/// <summary>Polls the feed on the <see cref="PollScheduler"/>'s schedule, archives and imports each new version.</summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopeFactory,
    PollScheduler scheduler,
    RawArchive archive,
    TimeProvider timeProvider,
    IOptions<FeedOptions> feedOptions,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);
    private const int MaxLoggedWarnings = 20;

    private FeedVersion? _lastImported;
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                CleanArchiveIfDue();

                var plan = scheduler.NextPoll();
                var wait = plan.At - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    logger.LogDebug("Next poll at {PollAt:O} ({Phase}).", plan.At, plan.Phase);
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }

                scheduler.OnPolled(timeProvider.GetUtcNow());
                await PollOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<ImportWriter>();
                var state = await writer.LoadStateAsync(feedOptions.Value.DelayHistorySize, cancellationToken);
                _lastImported = state.LastImported;
                scheduler.Initialize(state.LastImported?.GeneratedAt, state.RecentDelaysOldestFirst);
                logger.LogInformation(
                    "Ingest started. Last import generated at {LastGeneratedAt:O}; learned delay {LearnedDelaySeconds:F0} s from {DelayCount} imports.",
                    state.LastImported?.GeneratedAt, scheduler.LearnedDelay.TotalSeconds, state.RecentDelaysOldestFirst.Count);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not load the ingest state; retrying in {RetrySeconds} s.", StartupRetryDelay.TotalSeconds);
                await Task.Delay(StartupRetryDelay, timeProvider, cancellationToken);
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<FeedClient>();
            switch (await client.FetchAsync(_lastImported, cancellationToken))
            {
                case FetchResult.NotModified:
                    logger.LogDebug("Feed not modified.");
                    break;
                case FetchResult.Unchanged:
                    logger.LogDebug("Feed content unchanged.");
                    break;
                case FetchResult.Stale stale:
                    logger.LogInformation(
                        "Stale version ignored: generated at {GeneratedAt:O}, last import generated at {LastGeneratedAt:O}.",
                        stale.GeneratedAt, _lastImported?.GeneratedAt);
                    break;
                case FetchResult.NewVersion newVersion:
                    await ImportAsync(scope.ServiceProvider.GetRequiredService<ImportWriter>(), newVersion.Candidate, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Feed poll failed.");
        }
    }

    private async Task ImportAsync(ImportWriter writer, FeedCandidate candidate, CancellationToken cancellationToken)
    {
        var maxAttempts = feedOptions.Value.MaxImportAttempts;
        var failures = await writer.CountFailuresAsync(candidate.Sha256, cancellationToken);
        if (failures >= maxAttempts)
        {
            logger.LogError(
                "Skipping the feed generated at {GeneratedAt:O} (sha256 {Sha256}) after {Failures} failed imports.",
                candidate.GeneratedAt, candidate.Sha256, failures);
            // Remember it so it's discarded from now on, but keep the ETag of the last version actually imported.
            _lastImported = new FeedVersion(candidate.GeneratedAt, candidate.Sha256, _lastImported?.ETag);
            scheduler.OnNewVersion(candidate.GeneratedAt, delaySeconds: null);
            return;
        }

        string? archivePath = null;
        try
        {
            archivePath = await archive.SaveAsync(candidate.GzipBytes, candidate.GeneratedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not archive the feed generated at {GeneratedAt:O}; importing anyway.", candidate.GeneratedAt);
        }

        LogWarnings(candidate.Document.Warnings);

        try
        {
            var outcome = await writer.ImportAsync(candidate, archivePath, cancellationToken);
            _lastImported = new FeedVersion(candidate.GeneratedAt, candidate.Sha256, candidate.ETag);

            if (outcome is not { Import: { } import, Changes: { } changes })
            {
                logger.LogInformation("Feed generated at {GeneratedAt:O} was already imported by another worker.", candidate.GeneratedAt);
                scheduler.OnNewVersion(candidate.GeneratedAt, delaySeconds: null);
                return;
            }

            scheduler.OnNewVersion(candidate.GeneratedAt, candidate.DelaySeconds);
            LogWarnings(changes.Warnings);
            logger.LogInformation(
                "Imported feed generated at {GeneratedAt:O} (import {ImportId}, delay {DelaySeconds:F0} s): {StationsTotal} stations, " +
                "{StationsAdded} added, {StationsUpdated} updated ({StationsReactivated} reactivated), {StationsDeactivated} deactivated, " +
                "{PricesChanged} price changes, {Warnings} warnings. Next window opens {LearnedDelaySeconds:F0} s after the next slot.",
                import.GeneratedAt, import.Id, import.DelaySeconds, import.StationsTotal,
                import.StationsAdded, import.StationsUpdated, changes.ReactivatedCount, import.StationsDeactivated,
                import.PricesChanged, import.Warnings, scheduler.LearnedDelay.TotalSeconds);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Import of the feed generated at {GeneratedAt:O} failed (attempt {Attempt} of {MaxAttempts}).",
                candidate.GeneratedAt, failures + 1, maxAttempts);
            await LogFailureAsync(candidate, archivePath, ex, cancellationToken);
        }
    }

    private async Task LogFailureAsync(FeedCandidate candidate, string? archivePath, Exception error, CancellationToken cancellationToken)
    {
        try
        {
            // A fresh scope: the failed context may hold half-applied changes.
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ImportWriter>()
                .LogFailureAsync(candidate, archivePath, error, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not record the failed import.");
        }
    }

    private void LogWarnings(IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings.Take(MaxLoggedWarnings))
        {
            logger.LogWarning("Feed data warning: {Warning}", warning);
        }

        if (warnings.Count > MaxLoggedWarnings)
        {
            logger.LogWarning("… and {MoreWarnings} more feed data warnings.", warnings.Count - MaxLoggedWarnings);
        }
    }

    private void CleanArchiveIfDue()
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextCleanupAt)
        {
            return;
        }

        _nextCleanupAt = now + CleanupInterval;
        try
        {
            var deleted = archive.DeleteExpired();
            logger.LogInformation("Raw archive cleanup deleted {DeletedFiles} expired files.", deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Raw archive cleanup failed.");
        }
    }
}
