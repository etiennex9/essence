using System.Diagnostics;
using Essence.Data;
using Essence.Data.Entities;
using Essence.Ranking;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>
/// The recent price history in memory, what the trends read (<see cref="PriceWindow"/>, <see cref="TrendRules.Window"/>):
/// one copy shared by every request, about 13 MB, loaded by the first request that needs it (about a second) while the
/// others wait. Each new import adds only its rows, and the rows closed since; once a day what's older than the window is
/// dropped. Requests only read it, and an update makes a new copy, so how many ask doesn't change its size.
/// </summary>
public sealed class PriceTimeline(TimeProvider time, ILogger<PriceTimeline> logger)
{
    private readonly SemaphoreSlim _loading = new(1, 1);

    /// <summary>The window and the version it's of, swapped as one so a request never reads one with the other's.</summary>
    private volatile Loaded? _loaded;

    private DateOnly _trimmedOn;

    /// <summary>The history as of <paramref name="version"/>, the snapshot's (<see cref="Snapshot.Version"/>): unchanged since, no query.</summary>
    public async Task<PriceWindow> GetAsync(EssenceDbContext db, DateTimeOffset? version, CancellationToken cancellationToken)
    {
        if (version is null)
        {
            return PriceWindow.Empty;
        }

        if (_loaded is { } current && current.Version == version)
        {
            return current.Window;
        }

        await _loading.WaitAsync(cancellationToken);
        try
        {
            if (_loaded is { } loaded && loaded.Version == version)
            {
                return loaded.Window;
            }

            var now = time.GetUtcNow();
            var keepFrom = now - TrendRules.Window;
            var watch = Stopwatch.StartNew();
            PriceWindow window;
            if (_loaded?.Window is not { LastImport: { } last } previous)
            {
                window = await LoadAsync(db, keepFrom, cancellationToken);
                var (fuels, spans) = window.Size;
                logger.LogInformation(
                    "Loaded the price history for trends: {Spans} prices of {Fuels} station fuels, about {Megabytes} MB, in {Milliseconds} ms.",
                    spans, fuels, (spans * 24L + fuels * 96L) / 1_000_000, watch.ElapsedMilliseconds);
                _trimmedOn = DateOnly.FromDateTime(now.UtcDateTime);
            }
            else
            {
                window = await UpdateAsync(db, previous, last, keepFrom, cancellationToken);
                if (DateOnly.FromDateTime(now.UtcDateTime) != _trimmedOn)
                {
                    window = window.Trimmed(keepFrom);
                    _trimmedOn = DateOnly.FromDateTime(now.UtcDateTime);
                }
            }

            _loaded = new Loaded(window, version.Value);
            return window;
        }
        finally
        {
            _loading.Release();
        }
    }

    private sealed record Loaded(PriceWindow Window, DateTimeOffset Version);

    private static async Task<PriceWindow> LoadAsync(EssenceDbContext db, DateTimeOffset keepFrom, CancellationToken cancellationToken)
    {
        var imports = await ImportsAsync(db.Imports.Where(i => i.GeneratedAt >= keepFrom - TrendRules.Reach), cancellationToken);
        if (imports.Count == 0)
        {
            return PriceWindow.Empty;
        }

        var (start, firstId, lastId) = (imports[0].GeneratedAt, imports[0].Id, imports[^1].Id);
        var rows = await RowsAsync(db.PriceHistory.Where(p => p.ImportId >= firstId && p.ImportId <= lastId), cancellationToken);

        // Each fuel's price when the window starts: its last row before it, one index lookup per fuel by the (station, fuel,
        // valid_from) index. In SQL, since EF turns the same query in LINQ into a ROW_NUMBER over every older row.
        var before = await RowsAsync(
            db.PriceHistory.FromSql($"""
                SELECT h.* FROM current_prices AS c
                CROSS JOIN LATERAL (
                    SELECT * FROM price_history AS p
                    WHERE p.station_id = c.station_id AND p.fuel_type_id = c.fuel_type_id AND p.valid_from < {start}
                    ORDER BY p.valid_from DESC
                    LIMIT 1) AS h
                WHERE h.valid_to IS NULL OR h.valid_to > {start}
                """),
            cancellationToken);

        // Each before the window, then the window's in order: no copy sorted again (half a million rows).
        return PriceWindow.Empty.With(imports, before.Concat(rows), [], keepFrom);
    }

    private static async Task<PriceWindow> UpdateAsync(
        EssenceDbContext db, PriceWindow window, ImportTime last, DateTimeOffset keepFrom, CancellationToken cancellationToken)
    {
        var imports = await ImportsAsync(db.Imports.Where(i => i.GeneratedAt > last.GeneratedAt), cancellationToken);
        if (imports.Count == 0)
        {
            return window;
        }

        // Up to the last import read: one committing meanwhile is read at the next version.
        var (firstId, lastId) = (imports[0].Id, imports[^1].Id);
        var rows = await RowsAsync(db.PriceHistory.Where(p => p.ImportId >= firstId && p.ImportId <= lastId), cancellationToken);
        var open = window.OpenRowIds.ToList();
        var closed = await db.PriceHistory
            .Where(p => open.Contains(p.Id) && p.ValidTo != null)
            .Select(p => new ClosedRow(p.Id, p.ValidTo!.Value))
            .ToListAsync(cancellationToken);
        return window.With(imports, rows, closed, keepFrom);
    }

    private static async Task<List<ImportTime>> ImportsAsync(IQueryable<Import> imports, CancellationToken cancellationToken) =>
        await imports
            .Where(i => i.Status == ImportStatus.Succeeded)
            .OrderBy(i => i.GeneratedAt)
            .Select(i => new ImportTime(i.Id, i.GeneratedAt))
            .ToListAsync(cancellationToken);

    private static async Task<List<HistoryRow>> RowsAsync(IQueryable<PriceHistory> rows, CancellationToken cancellationToken) =>
        await rows
            .OrderBy(p => p.ValidFrom)
            .Select(p => new HistoryRow(p.Id, p.StationId, p.FuelTypeId, p.PriceCents, p.IsAvailable, p.ValidFrom, p.ValidTo))
            .ToListAsync(cancellationToken);
}
