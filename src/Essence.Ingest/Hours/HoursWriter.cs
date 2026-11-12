using Essence.Data;
using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Essence.Ingest.Hours;

/// <summary>The feed's active stations as matching reads them, and how recent the latest change to any station is.</summary>
/// <param name="UpdatedAt">The latest <c>stations.updated_at</c>: when it moves, the sources are matched again.</param>
public sealed record StationSet(IReadOnlyList<MatchStation> Stations, DateTimeOffset? UpdatedAt);

/// <summary>A run of a source, as recorded in <c>hours_imports</c>.</summary>
/// <param name="Rematch">Matched again from the download kept on disk: nothing was fetched.</param>
/// <param name="FetchedAt">When the download was made: the rows it adds or changes say so.</param>
public sealed record HoursRun(
    string Source,
    bool Rematch,
    DateTimeOffset StartedAt,
    string Version,
    DateTimeOffset FetchedAt,
    int Records,
    int Warnings);

/// <param name="Import">The run's row; null for a re-match that changed nothing, which isn't recorded.</param>
public sealed record HoursWriteOutcome(HoursImport? Import, HoursChanges Changes);

/// <summary>A source found off on a start, and how many of its rows were removed.</summary>
public sealed record DisabledSource(string Source, int Removed);

/// <summary>Database side of the hours: the stations to match, a source's history, and its rows replaced, or removed when it's off.</summary>
public sealed class HoursWriter(EssenceDbContext db, TimeProvider timeProvider)
{
    /// <summary>"EssHours" in ASCII: hours runs never overlap, and never wait for the feed's imports (<see cref="Storage.ImportWriter"/>'s key).</summary>
    private const long AdvisoryLockKey = 0x45_73_73_48_6F_75_72_73;

    private const int MaxErrorLength = 4000;

    public async Task<StationSet> LoadStationsAsync(CancellationToken cancellationToken)
    {
        // Read first: a station changed meanwhile is in the list, and only makes the next check match again for nothing.
        var updatedAt = await StationsUpdatedAtAsync(cancellationToken);
        var stations = await db.Stations
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new { s.Id, s.NaturalKey, s.Brand, s.Latitude, s.Longitude, s.Address, s.PostalCode })
            .ToListAsync(cancellationToken);
        return new StationSet(
            stations
                .Select(s => new MatchStation(
                    s.Id, s.NaturalKey, BrandFamily.Of(s.Brand), s.Latitude, s.Longitude, HoursMatcher.HouseNumber(s.Address),
                    StationKey.NormalizePostalCode(s.PostalCode)))
                .ToList(),
            updatedAt);
    }

    public Task<DateTimeOffset?> StationsUpdatedAtAsync(CancellationToken cancellationToken) =>
        db.Stations.MaxAsync(s => (DateTimeOffset?)s.UpdatedAt, cancellationToken);

    public Task<HoursSourceHistory> LoadHistoryAsync(string source, CancellationToken cancellationToken) =>
        HoursHistory.LoadAsync(db, source, cancellationToken);

    /// <summary>
    /// Replaces the source's rows with the run's matches, in one transaction: rows added, changed and removed, then the
    /// run's row in <c>hours_imports</c>, <see cref="HoursImportStatus.Unchanged"/> when nothing changed. A re-match that
    /// changes nothing writes nothing.
    /// </summary>
    public async Task<HoursWriteOutcome> WriteAsync(HoursRun run, IReadOnlyList<HoursMatch> matches, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);

        var rows = await db.StationHours.Where(h => h.Source == run.Source).ToDictionaryAsync(h => h.StationId, cancellationToken);
        var changes = HoursDiff.Compute(
            rows.ToDictionary(r => r.Key, r => HoursRow.Of(r.Value)),
            matches.ToDictionary(m => m.StationId, HoursRow.Of));
        if (run.Rematch && changes.Count == 0)
        {
            return new HoursWriteOutcome(null, changes);
        }

        var import = new HoursImport
        {
            Source = run.Source,
            StartedAt = run.StartedAt,
            FinishedAt = timeProvider.GetUtcNow(),
            Status = changes.Count == 0 ? HoursImportStatus.Unchanged
                : run.Rematch ? HoursImportStatus.Rematched
                : HoursImportStatus.Succeeded,
            SourceVersion = run.Version,
            Records = run.Records,
            Matched = matches.Count,
            Changed = changes.Count,
            Warnings = run.Warnings,
        };
        db.HoursImports.Add(import);

        foreach (var stationId in changes.Removed)
        {
            db.StationHours.Remove(rows[stationId]);
        }

        foreach (var (stationId, row) in changes.Updated)
        {
            var entry = rows[stationId];
            row.CopyTo(entry);
            entry.FetchedAt = run.FetchedAt;
            entry.HoursImport = import;
        }

        foreach (var (stationId, row) in changes.Added)
        {
            var entry = new StationHoursEntry
            {
                StationId = stationId,
                Source = run.Source,
                SourceRef = row.SourceRef,
                MatchedBy = row.MatchedBy,
                FetchedAt = run.FetchedAt,
                HoursImport = import,
            };
            row.CopyTo(entry);
            db.StationHours.Add(entry);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new HoursWriteOutcome(import, changes);
    }

    /// <summary>
    /// Removes the rows of every source that has runs but isn't in <paramref name="enabled"/> (turned off, or gone from the
    /// image), and records a <see cref="HoursImportStatus.Disabled"/> run for each, in one transaction. Nothing refreshes
    /// those rows any more, so they'd serve forever, outranking fresher sources, and the source would show as stale for
    /// good. A source already recorded as off, with no rows, is left alone.
    /// </summary>
    /// <returns>The sources recorded as off now.</returns>
    public async Task<IReadOnlyList<DisabledSource>> DisableAsync(IReadOnlySet<string> enabled, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);

        var disabled = new List<DisabledSource>();
        var sources = await db.HoursImports.Select(i => i.Source).Distinct().OrderBy(s => s).ToListAsync(cancellationToken);
        foreach (var source in sources.Where(s => !enabled.Contains(s)))
        {
            var latest = await db.HoursImports
                .Where(i => i.Source == source)
                .OrderByDescending(i => i.FinishedAt)
                .ThenByDescending(i => i.Id)
                .Select(i => i.Status)
                .FirstAsync(cancellationToken);
            var removed = await db.StationHours.Where(h => h.Source == source).ExecuteDeleteAsync(cancellationToken);
            if (latest == HoursImportStatus.Disabled && removed == 0)
            {
                continue;
            }

            var now = timeProvider.GetUtcNow();
            db.HoursImports.Add(new HoursImport
            {
                Source = source,
                StartedAt = now,
                FinishedAt = now,
                Status = HoursImportStatus.Disabled,
                Changed = removed,
            });
            disabled.Add(new DisabledSource(source, removed));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return disabled;
    }

    /// <summary>
    /// Records a run that wrote nothing: <see cref="HoursImportStatus.Failed"/> or <see cref="HoursImportStatus.Rejected"/>.
    /// The source's rows stay as they are. Call on a fresh context: a failed one may hold half-applied changes.
    /// </summary>
    public async Task<HoursImport> LogAsync(
        string source, HoursImportStatus status, DateTimeOffset startedAt, string? version, int records, int matched, int warnings,
        string error, CancellationToken cancellationToken)
    {
        var import = new HoursImport
        {
            Source = source,
            StartedAt = startedAt,
            FinishedAt = timeProvider.GetUtcNow(),
            Status = status,
            SourceVersion = version,
            Records = records,
            Matched = matched,
            Warnings = warnings,
            Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error,
        };
        db.HoursImports.Add(import);
        await db.SaveChangesAsync(cancellationToken);
        return import;
    }
}
