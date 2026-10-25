using Essence.Contracts;
using Essence.Data;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>An active station with coordinates and its current prices, as <see cref="StationSnapshot"/> keeps it.</summary>
/// <param name="MembersOnly">A Costco (<see cref="Brands.IsMembersOnly"/>).</param>
public sealed record SnapshotStation(
    long Id,
    string? Name,
    string? Brand,
    string Status,
    string Address,
    double Latitude,
    double Longitude,
    bool MembersOnly,
    IReadOnlyList<SnapshotPrice> Prices);

public sealed record SnapshotPrice(short FuelTypeId, decimal? PriceCents, bool IsAvailable);

/// <param name="Version">The import it was loaded for (<see cref="DataVersion"/>); null before the first one.</param>
public sealed record Snapshot(DateTimeOffset? Version, IReadOnlyList<SnapshotStation> Stations);

/// <summary>
/// Every active station with coordinates and its current prices, in memory: what Best near you ranks. About 2,460 stations,
/// loaded with one query when a new import is in, then shared by every request until the next one. The ranking filters it
/// (status, availability, Costco) itself: <c>/stations/nearby</c> returns only the 100 closest, which in Montréal cover a
/// few km, and doesn't check those.
/// </summary>
public sealed class StationSnapshot
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Snapshot? _current;

    /// <summary>The snapshot of the latest import, loaded by the first request that needs it while the others wait.</summary>
    public async Task<Snapshot> GetAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        // Read before the stations: an import committing in between makes the data newer than its version, which costs one
        // extra load, never stale data kept.
        var version = await DataVersion.LatestAsync(db, cancellationToken);
        if (_current is { } current && current.Version == version)
        {
            return current;
        }

        await _loading.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } loaded && loaded.Version == version)
            {
                return loaded;
            }

            var rows = await db.Stations
                .Where(s => s.IsActive && s.Latitude != null && s.Longitude != null)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Brand,
                    s.Status,
                    s.Address,
                    Latitude = s.Latitude!.Value,
                    Longitude = s.Longitude!.Value,
                    Prices = s.CurrentPrices.Select(p => new SnapshotPrice(p.FuelTypeId, p.PriceCents, p.IsAvailable)).ToList(),
                })
                .ToListAsync(cancellationToken);

            var stations = rows
                .Select(s => new SnapshotStation(
                    s.Id, s.Name, s.Brand, s.Status, s.Address, s.Latitude, s.Longitude, Brands.IsMembersOnly(s.Brand), s.Prices))
                .ToList();
            return _current = new Snapshot(version, stations);
        }
        finally
        {
            _loading.Release();
        }
    }
}
