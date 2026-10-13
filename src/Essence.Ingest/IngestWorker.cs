using Essence.Data.Entities;
using Essence.Ingest.Backfill;
using Essence.Ingest.Feed;
using Essence.Ingest.Scheduling;
using Essence.Ingest.Storage;
using Microsoft.Extensions.Options;

namespace Essence.Ingest;

/// <summary>
/// Polls the feed on the <see cref="PollScheduler"/>'s schedule, archives and imports each new version.
/// When a new version comes more than one slot after the last import, the versions in between are first backfilled from xlsx.
/// </summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopeFactory,
    PollScheduler scheduler,
    RawArchive archive,
    TimeProvider timeProvider,
    IOptions<FeedOptions> feedOptions,
    IOptions<BackfillOptions> backfillOptions,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);
    private const int MaxLoggedWarnings = 20;

    private const string ImportStats =
        "{StationsTotal} stations, {StationsAdded} added, {StationsUpdated} updated ({StationsReactivated} reactivated), " +
        "{StationsDeactivated} deactivated, {PricesChanged} price changes, {Warnings} warnings.";

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
                    // Older versions first: once the new one is imported, they could no longer be.
                    await BackfillAsync(client, newVersion.Candidate.GeneratedAt, cancellationToken);
                    await ImportAsync(newVersion.Candidate, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Feed poll failed.");
        }
    }

    /// <summary>
    /// Imports, from their xlsx snapshots, the versions published between the last import and <paramref name="newGeneratedAt"/>.
    /// Best effort: a slot whose file is gone or can't be fetched stays a gap.
    /// </summary>
    private async Task BackfillAsync(FeedClient client, DateTimeOffset newGeneratedAt, CancellationToken cancellationToken)
    {
        var options = backfillOptions.Value;
        if (!options.Enabled || _lastImported is not { } last)
        {
            return;
        }

        var plan = BackfillPlanner.MissedSlots(
            last.GeneratedAt, newGeneratedAt, timeProvider.GetUtcNow(), feedOptions.Value.PublishInterval, options.MaxAge);
        if (plan.TooOld > 0)
        {
            logger.LogWarning(
                "{TooOld} missed versions between {LastGeneratedAt:O} and {NewGeneratedAt:O} are older than {MaxAgeMinutes:F0} min and can't be backfilled.",
                plan.TooOld, last.GeneratedAt, newGeneratedAt, options.MaxAge.TotalMinutes);
        }

        if (plan.Slots.Count == 0)
        {
            return;
        }

        logger.LogInformation("Backfilling {Slots} missed versions from xlsx, {FirstSlot:O} to {LastSlot:O}.",
            plan.Slots.Count, plan.Slots[0], plan.Slots[^1]);
        int imported = 0, notFound = 0, failed = 0;
        foreach (var slot in plan.Slots)
        {
            FeedCandidate? candidate = null;
            try
            {
                foreach (var (fileName, generatedAt) in BackfillPlanner.FileCandidates(slot))
                {
                    candidate = await client.FetchXlsxAsync(new Uri(options.BaseUrl, fileName), generatedAt, cancellationToken);
                    if (candidate is not null)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not fetch the xlsx of the {Slot:O} version; skipped.", slot);
                failed++;
                continue;
            }

            if (candidate is null)
            {
                logger.LogInformation("No xlsx found for the {Slot:O} version.", slot);
                notFound++;
            }
            else if (candidate.GeneratedAt <= _lastImported?.GeneratedAt || candidate.GeneratedAt >= newGeneratedAt)
            {
                // Importing it would be refused, or would make the new version look stale.
                logger.LogWarning("The xlsx of the {Slot:O} version says it was generated at {GeneratedAt:O}; skipped.",
                    slot, candidate.GeneratedAt);
                failed++;
            }
            else if (await ImportAsync(candidate, cancellationToken))
            {
                imported++;
            }
            else
            {
                failed++;
            }
        }

        logger.LogInformation("Backfill finished: {Imported} imported, {NotFound} not found, {Failed} failed.", imported, notFound, failed);
    }

    /// <returns>False when the import failed, or the file was skipped after too many failures.</returns>
    private async Task<bool> ImportAsync(FeedCandidate candidate, CancellationToken cancellationToken)
    {
        // A scope per import: after a failure, the context may hold half-applied changes that the next import would save.
        await using var scope = scopeFactory.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<ImportWriter>();
        var maxAttempts = feedOptions.Value.MaxImportAttempts;
        var failures = await writer.CountFailuresAsync(candidate.Sha256, cancellationToken);
        if (failures >= maxAttempts)
        {
            logger.LogError(
                "Skipping the {Source} file generated at {GeneratedAt:O} (sha256 {Sha256}) after {Failures} failed imports.",
                Name(candidate), candidate.GeneratedAt, candidate.Sha256, failures);
            // Remember it so it's discarded from now on, but keep the ETag of the last version actually imported.
            _lastImported = new FeedVersion(candidate.GeneratedAt, candidate.Sha256, _lastImported?.ETag);
            scheduler.OnNewVersion(candidate.GeneratedAt, delaySeconds: null);
            return false;
        }

        string? archivePath = null;
        try
        {
            archivePath = await archive.SaveAsync(candidate.RawBytes, candidate.GeneratedAt, candidate.Source, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not archive the {Source} file generated at {GeneratedAt:O}; importing anyway.",
                Name(candidate), candidate.GeneratedAt);
        }

        LogWarnings(candidate.Document.Warnings);

        try
        {
            var outcome = await writer.ImportAsync(candidate, archivePath, cancellationToken);
            _lastImported = new FeedVersion(candidate.GeneratedAt, candidate.Sha256, candidate.ETag);

            if (outcome is not { Import: { } import, Changes: { } changes })
            {
                logger.LogInformation("The {Source} file generated at {GeneratedAt:O} was already imported by another worker.",
                    Name(candidate), candidate.GeneratedAt);
                scheduler.OnNewVersion(candidate.GeneratedAt, delaySeconds: null);
                return true;
            }

            scheduler.OnNewVersion(candidate.GeneratedAt, candidate.DelaySeconds);
            LogWarnings(changes.Warnings);
            object?[] stats =
            [
                import.StationsTotal, import.StationsAdded, import.StationsUpdated, changes.ReactivatedCount,
                import.StationsDeactivated, import.PricesChanged, import.Warnings,
            ];
            if (candidate.Source == ImportSource.Xlsx)
            {
                // No delay to report, and the next window only matters once the live version is imported.
                logger.LogInformation("Backfilled the {Source} file generated at {GeneratedAt:O} (import {ImportId}): " + ImportStats,
                    [Name(candidate), import.GeneratedAt, import.Id, .. stats]);
            }
            else
            {
                logger.LogInformation(
                    "Imported the {Source} file generated at {GeneratedAt:O} (import {ImportId}, delay {DelaySeconds:F0} s): " + ImportStats +
                    " Next window opens {LearnedDelaySeconds:F0} s after the next slot.",
                    [Name(candidate), import.GeneratedAt, import.Id, import.DelaySeconds, .. stats, scheduler.LearnedDelay.TotalSeconds]);
            }

            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Import of the {Source} file generated at {GeneratedAt:O} failed (attempt {Attempt} of {MaxAttempts}).",
                Name(candidate), candidate.GeneratedAt, failures + 1, maxAttempts);
            await LogFailureAsync(candidate, archivePath, ex, cancellationToken);
            return false;
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

    /// <summary>"feed" or "xlsx", as stored in <c>imports.source</c>.</summary>
    private static string Name(FeedCandidate candidate) => candidate.Source.ToString().ToLowerInvariant();

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
