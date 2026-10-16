using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api.Endpoints;

public static class StationEndpoints
{
    private const int MaxPageSize = 200;
    private const double MaxRadiusKm = 50;
    private const int MaxNearbyLimit = 100;
    private const int DefaultHistoryDays = 30;
    private const int MaxHistoryDays = 366;

    private static readonly Expression<Func<Station, StationSummary>> ToSummary = s => new StationSummary(
        s.Id, s.Name, s.Brand, s.Status, s.Address, s.PostalCode, s.Region, s.Latitude, s.Longitude, s.IsActive);

    public static IEndpointRouteBuilder MapStationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stations").WithTags("Stations").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/", ListStations)
            .WithSummary("List stations")
            .WithDescription("Filters combine with AND. `q` searches the name, and the address and postal code ignoring case and accents.");
        group.MapGet("/nearby", Nearby)
            .WithSummary("Active stations near a point, closest first")
            .WithDataVersion();
        group.MapGet("/{id:long}", GetStation)
            .WithSummary("A station with its current prices")
            .WithDataVersion();
        group.MapGet("/{id:long}/prices", GetPriceHistory)
            .WithSummary("Price history of a station")
            .WithDescription("Every price state that overlaps [from, to). Defaults to the last 30 days.");
        return app;
    }

    private static async Task<Results<Ok<PagedList<StationSummary>>, ValidationProblem>> ListStations(
        EssenceDbContext db,
        CancellationToken cancellationToken,
        [Description("Exact region name, case-insensitive, e.g. Montréal.")] string? region = null,
        [Description("Exact brand, case-insensitive, e.g. Shell.")] string? brand = null,
        [Description("true: only stations still in the feed; false: only stations that disappeared.")] bool? active = null,
        [Description("Text to search in the name, address and postal code.")] string? q = null,
        int page = 1,
        int pageSize = 50)
    {
        var errors = new QueryErrors()
            .Require(page >= 1, nameof(page), "Must be 1 or more.")
            .Require(pageSize is >= 1 and <= MaxPageSize, nameof(pageSize), $"Must be between 1 and {MaxPageSize}.");
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var query = db.Stations.AsQueryable();
        if (!string.IsNullOrWhiteSpace(region))
        {
            var lowered = region.Trim().ToLowerInvariant();
            query = query.Where(s => s.Region.ToLower() == lowered);
        }

        if (!string.IsNullOrWhiteSpace(brand))
        {
            var lowered = brand.Trim().ToLowerInvariant();
            query = query.Where(s => s.Brand!.ToLower() == lowered);
        }

        if (active is { } isActive)
        {
            query = query.Where(s => s.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // The natural key holds the folded address ("230 av. murdoch, …") and the compact postal code ("J9X1E7").
            var namePattern = "%" + EscapeLike(q.Trim()) + "%";
            var address = StationKey.NormalizeAddress(q);
            var postalCode = StationKey.NormalizePostalCode(q);
            query = query.Where(s =>
                EF.Functions.ILike(s.Name!, namePattern, @"\")
                || s.NaturalKey.Contains(address)
                || s.NaturalKey.Contains(postalCode));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToSummary)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedList<StationSummary>(items, page, pageSize, total));
    }

    private static async Task<Results<Ok<StationDetail>, NotFound>> GetStation(
        EssenceDbContext db,
        long id,
        CancellationToken cancellationToken)
    {
        var station = await db.Stations
            .Where(s => s.Id == id)
            .Select(s => new StationDetail(
                s.Id, s.Name, s.Brand, s.Status, s.Address, s.PostalCode, s.Region, s.Latitude, s.Longitude, s.IsActive,
                s.FirstSeenAt, s.LastSeenAt, s.UpdatedAt,
                s.CurrentPrices
                    .OrderBy(p => p.FuelTypeId)
                    .Select(p => new CurrentPriceDto(p.FuelType.Code, p.PriceCents, p.IsAvailable, p.Since))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);
        return station is null ? TypedResults.NotFound() : TypedResults.Ok(station);
    }

    private static async Task<Results<Ok<List<NearbyStation>>, ValidationProblem>> Nearby(
        EssenceDbContext db,
        [Description("Latitude of the centre, in degrees.")] double lat,
        [Description("Longitude of the centre, in degrees.")] double lon,
        CancellationToken cancellationToken,
        [Description("Search radius in kilometres (max 50).")] double radiusKm = 5,
        [Description("Only stations selling this fuel (Régulier, Super, Diesel; accents optional), with only that price.")] string? fuel = null,
        int limit = 20)
    {
        var errors = new QueryErrors()
            .Require(lat is >= -90 and <= 90, nameof(lat), "Must be between -90 and 90.")
            .Require(lon is >= -180 and <= 180, nameof(lon), "Must be between -180 and 180.")
            .Require(radiusKm is > 0 and <= MaxRadiusKm, nameof(radiusKm), $"Must be more than 0 and at most {MaxRadiusKm}.")
            .Require(limit is >= 1 and <= MaxNearbyLimit, nameof(limit), $"Must be between 1 and {MaxNearbyLimit}.");
        var fuelType = await ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        // earth_box is the index-assisted prefilter (GiST on ll_to_earth); earth_distance makes it an exact circle.
        // Fuel type ids start at 1, so 0 means "any fuel".
        var radiusMeters = radiusKm * 1000;
        short fuelId = fuelType?.Id ?? 0;
        var hits = await db.Database.SqlQuery<NearbyHit>($"""
            SELECT s.id, earth_distance(ll_to_earth({lat}, {lon}), ll_to_earth(s.latitude, s.longitude)) AS distance_m
            FROM stations s
            WHERE s.is_active
              AND s.latitude IS NOT NULL AND s.longitude IS NOT NULL
              AND earth_box(ll_to_earth({lat}, {lon}), {radiusMeters}) @> ll_to_earth(s.latitude, s.longitude)
              AND earth_distance(ll_to_earth({lat}, {lon}), ll_to_earth(s.latitude, s.longitude)) <= {radiusMeters}
              AND ({fuelId} = 0 OR EXISTS (
                    SELECT 1 FROM current_prices cp WHERE cp.station_id = s.id AND cp.fuel_type_id = {fuelId}))
            ORDER BY distance_m
            LIMIT {limit}
            """).ToListAsync(cancellationToken);

        var ids = hits.Select(h => h.Id).ToList();
        var stations = await db.Stations
            .Where(s => ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Brand,
                s.Address,
                s.PostalCode,
                s.Region,
                s.Latitude,
                s.Longitude,
                Prices = s.CurrentPrices
                    .Where(p => fuelId == 0 || p.FuelTypeId == fuelId)
                    .OrderBy(p => p.FuelTypeId)
                    .Select(p => new CurrentPriceDto(p.FuelType.Code, p.PriceCents, p.IsAvailable, p.Since))
                    .ToList(),
            })
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var result = hits
            .Where(h => stations.ContainsKey(h.Id))
            .Select(h =>
            {
                var s = stations[h.Id];
                return new NearbyStation(
                    s.Id, s.Name, s.Brand, s.Address, s.PostalCode, s.Region, s.Latitude!.Value, s.Longitude!.Value,
                    Math.Round(h.DistanceMeters / 1000, 3), s.Prices);
            })
            .ToList();
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<List<PriceHistoryDto>>, NotFound, ValidationProblem>> GetPriceHistory(
        EssenceDbContext db,
        TimeProvider timeProvider,
        long id,
        CancellationToken cancellationToken,
        [Description("Only this fuel (accents optional).")] string? fuel = null,
        [Description("Start of the range (ISO 8601). Defaults to 30 days before `to`.")] DateTimeOffset? from = null,
        [Description("End of the range (ISO 8601). Defaults to now.")] DateTimeOffset? to = null)
    {
        var end = (to ?? timeProvider.GetUtcNow()).ToUniversalTime();
        var start = (from ?? end.AddDays(-DefaultHistoryDays)).ToUniversalTime();
        var errors = new QueryErrors()
            .Require(start < end, nameof(from), "Must be before `to`.")
            .Require(end - start <= TimeSpan.FromDays(MaxHistoryDays), nameof(to), $"The range can't exceed {MaxHistoryDays} days.");
        var fuelType = await ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        if (!await db.Stations.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var query = db.PriceHistory.Where(p => p.StationId == id && p.ValidFrom < end && (p.ValidTo == null || p.ValidTo > start));
        if (fuelType is not null)
        {
            var fuelId = fuelType.Id;
            query = query.Where(p => p.FuelTypeId == fuelId);
        }

        var rows = await query
            .OrderBy(p => p.FuelTypeId)
            .ThenBy(p => p.ValidFrom)
            .Select(p => new PriceHistoryDto(p.FuelType.Code, p.PriceCents, p.IsAvailable, p.ValidFrom, p.ValidTo))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(rows);
    }

    /// <summary>Resolves an optional <c>fuel</c> parameter, recording an error if it's unknown.</summary>
    internal static async Task<FuelType?> ResolveFuelAsync(EssenceDbContext db, string? fuel, QueryErrors errors, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fuel))
        {
            return null;
        }

        var fuelType = await FuelLookup.FindAsync(db, fuel, cancellationToken);
        if (fuelType is null)
        {
            errors.Require(false, nameof(fuel), $"Unknown fuel \"{fuel}\". {await FuelLookup.DescribeKnownAsync(db, cancellationToken)}");
        }

        return fuelType;
    }

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}

/// <summary>Row of the nearby query. Column names are explicit because it's raw SQL.</summary>
internal sealed class NearbyHit
{
    [Column("id")]
    public long Id { get; set; }

    [Column("distance_m")]
    public double DistanceMeters { get; set; }
}
