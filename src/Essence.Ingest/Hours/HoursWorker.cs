using Essence.Data.Entities;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// Fetches each hours source on the <see cref="HoursSchedule"/>'s schedule (weekly, at night), matches its records to the
/// feed's stations and writes the source's rows, one source at a time. Every <see cref="HoursOptions.RematchEvery"/>, the
/// sources' last downloads are matched again when the feed's stations changed, so a station added or moved gets its hours
/// within the hour without asking the source. A failed or rejected run leaves the source's rows as they were.
/// </summary>
public sealed class HoursWorker(
    IServiceScopeFactory scopeFactory,
    HoursSchedule schedule,
    HoursCache cache,
    TimeProvider timeProvider,
    IOptions<HoursOptions> options,
    ILogger<HoursWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxLoggedWarnings = 20;

    /// <summary>
    /// A run's counts. The stations matched are those the gate counts (<see cref="MatchResult.Counted"/>), those by address
    /// among them; those matched by location only, which get no hours, come apart.
    /// </summary>
    private const string RunStats =
        "{Records} records, {Matched} stations matched ({ByAddress} of them by address) and {ByLocation} by location only, without hours " +
        "({Unmatched} records unmatched, {Ambiguous} ambiguous), {Added} added, {Updated} updated, {Removed} removed, {Warnings} warnings";

    private readonly List<SourceState> _sources = [];
    private DateTimeOffset _startedAt;
    private DateTimeOffset _nextRematchAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("The hours import is off (Hours:Enabled).");
            return;
        }

        try
        {
            await InitializeAsync(stoppingToken);
            if (_sources.Count == 0)
            {
                logger.LogInformation("The hours import has no source turned on.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var next = _sources.MinBy(s => s.NextRunAt)!;
                var wakeAt = next.NextRunAt < _nextRematchAt ? next.NextRunAt : _nextRematchAt;
                var wait = wakeAt - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }

                var now = timeProvider.GetUtcNow();
                foreach (var source in _sources.Where(s => s.NextRunAt <= now).ToList())
                {
                    await RunAsync(source, stoppingToken);
                }

                if (_nextRematchAt <= timeProvider.GetUtcNow())
                {
                    await RematchAsync(stoppingToken);
                    _nextRematchAt = timeProvider.GetUtcNow() + options.Value.RematchEvery;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _startedAt = timeProvider.GetUtcNow();
        while (true)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<HoursWriter>();
                var enabled = scope.ServiceProvider.GetServices<IHoursSource>().Where(s => s.Enabled).ToList();
                foreach (var source in await writer.DisableAsync(enabled.Select(s => s.Name).ToHashSet(StringComparer.Ordinal), cancellationToken))
                {
                    logger.LogInformation(
                        "The hours source {Source} is off, or gone from the image: its {Removed} rows were removed, and it's no longer checked.",
                        source.Source, source.Removed);
                }

                _sources.Clear();
                foreach (var source in enabled)
                {
                    var history = await writer.LoadHistoryAsync(source.Name, cancellationToken);
                    var state = new SourceState(source.Name, _sources.Count)
                    {
                        Runs = new SourceRuns(history.LastSuccess?.FinishedAt, history.LastAttempt?.FinishedAt, history.FailuresSinceSuccess),
                    };
                    state.NextRunAt = source.ShippedInImage ? _startedAt : schedule.NextRun(state.Runs, _startedAt, state.Index);
                    _sources.Add(state);
                    logger.LogInformation(
                        "Hours from {Source}: last success {LastSuccessAt:O}, {Failures} failures since; next run at {NextRunAt:O}.",
                        source.Name, state.Runs.LastSuccessAt, state.Runs.FailuresSinceSuccess, state.NextRunAt);
                }

                // The first re-match comes after the runs due now: it catches the stations that changed while the worker was down.
                _nextRematchAt = _startedAt;
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not load the hours sources' state; retrying in {RetrySeconds} s.", StartupRetryDelay.TotalSeconds);
                await Task.Delay(StartupRetryDelay, timeProvider, cancellationToken);
            }
        }
    }

    /// <summary>Fetches, reads, matches and writes one source; any failure is recorded and tried again later.</summary>
    private async Task RunAsync(SourceState state, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        string? version = null;
        int records = 0, matched = 0, warnings = 0;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var source = Source(scope, state.Name);
            var writer = scope.ServiceProvider.GetRequiredService<HoursWriter>();

            var cached = await LoadCacheAsync(state.Name, cancellationToken);
            byte[] raw;
            switch (await source.FetchAsync(cached?.Version, cancellationToken))
            {
                case HoursFetch.Fetched fetched:
                    (raw, version) = (fetched.Raw, fetched.Version);
                    break;
                case HoursFetch.NotModified when cached is not null:
                    (raw, version) = (cached.Raw, cached.Version);
                    break;
                default:
                    throw new InvalidOperationException($"{state.Name} said its hours weren't modified, but none are kept on disk.");
            }

            var fetchedAt = timeProvider.GetUtcNow();
            var parsed = source.Parse(raw);
            (records, warnings) = (parsed.Records.Count, parsed.Warnings.Count);
            LogWarnings(state.Name, parsed.Warnings);

            var stations = await writer.LoadStationsAsync(cancellationToken);
            var match = HoursMatcher.Match(parsed.Records, stations.Stations, source.MatchRadiusM, source.OtherBrandReachM);
            matched = match.Counted;
            var history = await writer.LoadHistoryAsync(state.Name, cancellationToken);
            if (HoursGate.Check(source, parsed, matched, history.LastGood?.Matched, options.Value.MinShareOfLastGood) is { } reason)
            {
                await writer.LogAsync(state.Name, HoursImportStatus.Rejected, startedAt, version, records, matched, warnings, reason, cancellationToken);
                state.OnFailure(timeProvider.GetUtcNow());
                state.NextRunAt = schedule.NextRun(state.Runs, _startedAt, state.Index);
                logger.LogWarning(
                    "Hours from {Source} rejected: {Reason} The last good hours keep serving; trying again at {NextRunAt:O} (failure {Failures} in a row).",
                    state.Name, reason, state.NextRunAt, state.Runs.FailuresSinceSuccess);
                LogIfStale(state);
                return;
            }

            var outcome = await writer.WriteAsync(
                new HoursRun(state.Name, Rematch: false, startedAt, version, fetchedAt, records, warnings), match.Matches, cancellationToken);
            await SaveCacheAsync(state.Name, raw, version, fetchedAt, cached?.Version);

            state.OnSuccess(timeProvider.GetUtcNow(), stations.UpdatedAt);
            state.NextRunAt = schedule.NextRun(state.Runs, _startedAt, state.Index);
            logger.LogInformation(
                "Hours from {Source} {Status} (hours import {HoursImportId}, version {Version}): " + RunStats + ". Next run at {NextRunAt:O}.",
                [
                    state.Name, Name(outcome.Import!.Status), outcome.Import.Id, version, records, matched, match.ByAddress, match.ByLocation,
                    match.Unmatched, match.Ambiguous, outcome.Changes.Added.Count, outcome.Changes.Updated.Count, outcome.Changes.Removed.Count,
                    warnings, state.NextRunAt,
                ]);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.OnFailure(timeProvider.GetUtcNow());
            state.NextRunAt = schedule.NextRun(state.Runs, _startedAt, state.Index);
            logger.LogError(ex, "Hours from {Source} failed (failure {Failures} in a row); trying again at {NextRunAt:O}.",
                state.Name, state.Runs.FailuresSinceSuccess, state.NextRunAt);
            LogIfStale(state);
            await LogFailureAsync(state.Name, startedAt, version, records, matched, warnings, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Matches each source's last download again when the feed's stations changed since it was last matched. Nothing is
    /// fetched, so it never counts as a success: a re-match that changes rows is recorded as
    /// <see cref="HoursImportStatus.Rematched"/>, one that changes nothing isn't recorded, and one the gate rejects is
    /// only logged, and left to the next run. So is a download on disk that isn't the one serving (keeping the newer one
    /// failed): matching it would put older hours back. One that fails is tried again at the next check.
    /// </summary>
    private async Task RematchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? updatedAt;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            updatedAt = await scope.ServiceProvider.GetRequiredService<HoursWriter>().StationsUpdatedAtAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not tell whether the stations changed; the hours are matched again later.");
            return;
        }

        foreach (var state in _sources.Where(s => s.MatchedUpTo is null || updatedAt > s.MatchedUpTo))
        {
            var startedAt = timeProvider.GetUtcNow();
            try
            {
                if (await LoadCacheAsync(state.Name, cancellationToken) is not { } cached)
                {
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var source = Source(scope, state.Name);
                var writer = scope.ServiceProvider.GetRequiredService<HoursWriter>();
                var history = await writer.LoadHistoryAsync(state.Name, cancellationToken);
                if (!history.Serves(cached.Version))
                {
                    state.MatchedUpTo = updatedAt;
                    logger.LogWarning(
                        "Hours from {Source} not matched again: the download kept on disk ({CachedVersion}) isn't the one serving ({ServingVersion}). " +
                        "The hours serving stay; the next run keeps its download.",
                        state.Name, cached.Version, history.LastGood?.SourceVersion ?? "none");
                    continue;
                }

                var parsed = source.Parse(cached.Raw);
                var stations = await writer.LoadStationsAsync(cancellationToken);
                var match = HoursMatcher.Match(parsed.Records, stations.Stations, source.MatchRadiusM, source.OtherBrandReachM);
                if (HoursGate.Check(source, parsed, match.Counted, history.LastGood?.Matched, options.Value.MinShareOfLastGood) is { } reason)
                {
                    state.MatchedUpTo = stations.UpdatedAt;
                    logger.LogWarning("Hours from {Source} not matched again: {Reason} The last good hours keep serving.", state.Name, reason);
                    continue;
                }

                var outcome = await writer.WriteAsync(
                    new HoursRun(state.Name, Rematch: true, startedAt, cached.Version, cached.FetchedAt, parsed.Records.Count, parsed.Warnings.Count),
                    match.Matches,
                    cancellationToken);
                // Only once written: a write that failed is tried again at the next check, stations changed since or not.
                state.MatchedUpTo = stations.UpdatedAt;
                if (outcome.Import is { } import)
                {
                    logger.LogInformation(
                        "Hours from {Source} matched again, the stations having changed (hours import {HoursImportId}): " + RunStats + ".",
                        [
                            state.Name, import.Id, parsed.Records.Count, match.Counted, match.ByAddress, match.ByLocation, match.Unmatched,
                            match.Ambiguous, outcome.Changes.Added.Count, outcome.Changes.Updated.Count, outcome.Changes.Removed.Count,
                            parsed.Warnings.Count,
                        ]);
                }
                else
                {
                    logger.LogDebug("Hours from {Source} matched again: nothing changed.", state.Name);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Hours from {Source} could not be matched again; trying at the next check.", state.Name);
            }
        }
    }

    private static IHoursSource Source(AsyncServiceScope scope, string name) =>
        scope.ServiceProvider.GetServices<IHoursSource>().Single(s => s.Name == name);

    private async Task<CachedDownload?> LoadCacheAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            return await cache.LoadAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the last download of {Source} kept on disk.", source);
            return null;
        }
    }

    /// <summary>
    /// Keeps the download once it's written, unless it's the one already kept: a re-match then needs no network. Its rows
    /// are in, so it's kept even when shutting down (a few KB): one missing leaves no re-match until a run keeps one, the
    /// older one kept in its place being refused (<see cref="Data.HoursSourceHistory.Serves"/>).
    /// </summary>
    private async Task SaveCacheAsync(string source, byte[] raw, string version, DateTimeOffset fetchedAt, string? cachedVersion)
    {
        if (version == cachedVersion)
        {
            return;
        }

        try
        {
            await cache.SaveAsync(source, raw, version, fetchedAt, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex, "Could not keep the download of {Source} on disk: it isn't matched again when the stations change until a run keeps one.", source);
        }
    }

    private async Task LogFailureAsync(
        string source, DateTimeOffset startedAt, string? version, int records, int matched, int warnings, Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            // A fresh scope: the failed context may hold half-applied changes.
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<HoursWriter>().LogAsync(
                source, HoursImportStatus.Failed, startedAt, version, records, matched, warnings, error.ToString(), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not record the failed hours run of {Source}.", source);
        }
    }

    /// <summary>Says so when the source's last success is older than <see cref="HoursOptions.StaleAfter"/>: its hours are stale.</summary>
    private void LogIfStale(SourceState state)
    {
        if (state.Runs.LastSuccessAt is { } at && timeProvider.GetUtcNow() - at > options.Value.StaleAfter)
        {
            logger.LogError(
                "Hours from {Source} have had no success since {LastSuccessAt:O}, more than {StaleAfterDays:F0} days: they're stale.",
                state.Name, at, options.Value.StaleAfter.TotalDays);
        }
    }

    private void LogWarnings(string source, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings.Take(MaxLoggedWarnings))
        {
            logger.LogWarning("Hours data warning from {Source}: {Warning}", source, warning);
        }

        if (warnings.Count > MaxLoggedWarnings)
        {
            logger.LogWarning("… and {MoreWarnings} more hours data warnings from {Source}.", warnings.Count - MaxLoggedWarnings, source);
        }
    }

    /// <summary>"succeeded", as stored.</summary>
    private static string Name(HoursImportStatus status) => status.ToString().ToLowerInvariant();

    private sealed class SourceState(string name, int index)
    {
        public string Name { get; } = name;

        /// <summary>The source's place among the sources, which staggers their first runs.</summary>
        public int Index { get; } = index;

        public SourceRuns Runs { get; set; } = new(null, null, 0);
        public DateTimeOffset NextRunAt { get; set; }

        /// <summary>The stations' latest <c>updated_at</c> when the source was last matched; null until it is in this process.</summary>
        public DateTimeOffset? MatchedUpTo { get; set; }

        public void OnSuccess(DateTimeOffset at, DateTimeOffset? stationsUpdatedAt)
        {
            Runs = new SourceRuns(at, at, 0);
            MatchedUpTo = stationsUpdatedAt;
        }

        public void OnFailure(DateTimeOffset at) => Runs = Runs with { LastAttemptAt = at, FailuresSinceSuccess = Runs.FailuresSinceSuccess + 1 };
    }
}
