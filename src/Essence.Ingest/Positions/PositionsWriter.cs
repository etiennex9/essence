using Essence.Data;
using Essence.Data.Entities;
using Essence.Ingest.Hours;
using Microsoft.EntityFrameworkCore;

namespace Essence.Ingest.Positions;

/// <summary>The feed's active stations as finding where they are reads them, at the feed's points.</summary>
/// <param name="Stations">Those with a point.</param>
/// <param name="FeedPoints">Every active station's feed point by natural key, null for none: what the corrections are checked against.</param>
public sealed record PositionStations(
    IReadOnlyList<PositionStation> Stations, IReadOnlyDictionary<string, (double? Latitude, double? Longitude)> FeedPoints);

/// <summary>Database side of the positions: the stations at the feed's points, and the candidates replaced as a whole.</summary>
public sealed class PositionsWriter(EssenceDbContext db)
{
    /// <summary>The active stations, at the feed's points: never corrected, since a correction is checked against them.</summary>
    public async Task<PositionStations> LoadStationsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Stations
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new { s.Id, s.NaturalKey, s.Name, s.Brand, s.Address, s.PostalCode, s.Latitude, s.Longitude })
            .ToListAsync(cancellationToken);
        var stations = rows
            .Where(s => s.Latitude is not null && s.Longitude is not null)
            .Select(s => new PositionStation(
                s.Id, s.NaturalKey, BrandFamily.Of(s.Brand), s.Latitude!.Value, s.Longitude!.Value, HoursMatcher.HouseNumber(s.Address),
                StationKey.NormalizePostalCode(s.PostalCode), s.Address)
            {
                StoreNumber = HoursMatcher.StoreNumber(s.Name),
            })
            .ToList();
        var feedPoints = new Dictionary<string, (double?, double?)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            feedPoints.TryAdd(row.NaturalKey, (row.Latitude, row.Longitude));
        }

        return new PositionStations(stations, feedPoints);
    }

    /// <summary>Replaces every candidate with <paramref name="found"/>, in one transaction: a station no longer found leaves the list.</summary>
    public async Task ReplaceAsync(IReadOnlyList<FoundPosition> found, DateTimeOffset foundAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PositionCandidates.ExecuteDeleteAsync(cancellationToken);
        db.PositionCandidates.AddRange(found.Select(f => new PositionCandidate
        {
            StationId = f.Station.Id,
            NaturalKey = f.Station.NaturalKey,
            FeedLatitude = f.Station.Latitude,
            FeedLongitude = f.Station.Longitude,
            Latitude = f.Latitude,
            Longitude = f.Longitude,
            DistanceM = f.DistanceM,
            Evidence = PositionCandidate.Write(f.Evidence),
            FoundAt = foundAt,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
