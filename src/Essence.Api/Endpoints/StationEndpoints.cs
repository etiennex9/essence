using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Essence.Ranking;
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
    internal const double MaxFillLitres = 200;
    internal const double MaxLitresPer100Km = 40;
    internal const double MaxDetourMinutes = 120;

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
        group.MapGet("/best", Best)
            .WithSummary("Where to fill up near a point")
            .WithDescription(
                "Ranks the operating stations with an available price by what the fill-up costs once the gas burned getting there "
                + "and back is counted. The closest is the reference the savings are counted against. A farther station is picked "
                + "only if it saves at least $0.25 and its round trip takes at most `maxDetourMin` longer than the closest's; the "
                + "others stay listed, with the reason. Distances are by road (the 10 nearest and every station cheaper than the "
                + "nearest, at most 200). When the routing server is down, or the point is more than 1 km from a road, they're "
                + "estimated instead (`byRoad` false): straight line × 1.3, driven at 40 km/h, and a farther station must also "
                + "save with roads twice the straight line. Looks within `radiusKm`, widened to 25 km with fewer than 5 stations, "
                + "else returns the closest at any distance.")
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

    private static async Task<Results<Ok<BestNearby>, ValidationProblem>> Best(
        EssenceDbContext db,
        StationSnapshot snapshot,
        OsrmClient osrm,
        ILoggerFactory loggers,
        [Description("Latitude of the driver, in degrees.")] double lat,
        [Description("Longitude of the driver, in degrees.")] double lon,
        CancellationToken cancellationToken,
        [Description("Régulier, Super or Diesel (accents optional). Required.")] string? fuel = null,
        [Description("Litres bought at a stop.")] double fillL = 40,
        [Description("The car's consumption, in L/100 km.")] double per100Km = 8.5,
        [Description("true to include Costco, which sells to members only.")] bool costco = false,
        [Description("The longest detour: how many minutes longer than the closest's a station's round trip may take to be picked.")]
        double maxDetourMin = 10,
        [Description("Search radius in kilometres (max 25).")] double radiusKm = SearchArea.DefaultRadiusKm)
    {
        var errors = new QueryErrors()
            .Require(lat is >= -90 and <= 90, nameof(lat), "Must be between -90 and 90.")
            .Require(lon is >= -180 and <= 180, nameof(lon), "Must be between -180 and 180.")
            .Require(!string.IsNullOrWhiteSpace(fuel), nameof(fuel), "Required.")
            .Require(fillL is > 0 and <= MaxFillLitres, nameof(fillL), $"Must be more than 0 and at most {MaxFillLitres}.")
            .Require(per100Km is > 0 and <= MaxLitresPer100Km, nameof(per100Km), $"Must be more than 0 and at most {MaxLitresPer100Km}.")
            .Require(maxDetourMin is >= 0 and <= MaxDetourMinutes, nameof(maxDetourMin), $"Must be between 0 and {MaxDetourMinutes}.")
            .Require(radiusKm is > 0 and <= SearchArea.MaxRadiusKm, nameof(radiusKm), $"Must be more than 0 and at most {SearchArea.MaxRadiusKm}.");
        var fuelType = await ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any || fuelType is null)
        {
            return errors.ToProblem();
        }

        var fuelId = fuelType.Id;
        var stations = (await snapshot.GetAsync(db, cancellationToken)).Stations
            .Select(s => (Station: s, Price: s.Prices.FirstOrDefault(p => p.FuelTypeId == fuelId)))
            .Where(x => x.Price is { } price
                && Eligibility.IsEligible(x.Station.Status, price.IsAvailable, price.PriceCents, x.Station.MembersOnly, costco))
            .Select(x => (
                x.Station,
                PriceCents: x.Price!.PriceCents!.Value,
                Km: Geo.DistanceKm(lat, lon, x.Station.Latitude, x.Station.Longitude)))
            .ToList();
        var (area, areaRadiusKm) = SearchArea.Select(stations, s => s.Km, radiusKm);

        // By road when the routing server answers; road and estimated figures are never mixed in one ranking.
        var car = new Car((decimal)fillL, (decimal)per100Km);
        var byRoad = await RankByRoadAsync(osrm, loggers, new LatLon(lat, lon), area, car, maxDetourMin, cancellationToken);
        var ranking = byRoad
            ?? NearYou.Rank(area.Select(s => NearYou.Estimate(s.Station.Id, s.PriceCents, s.Km)).ToList(), car, maxDetourMin, byRoad: false);
        if (ranking is null)
        {
            return TypedResults.Ok(new BestNearby(null, null, null, null, [], areaRadiusKm, ByRoad: false));
        }

        var byId = area.ToDictionary(s => s.Station.Id, s => s.Station);
        RankedStation ToRanked(RankedCandidate ranked)
        {
            var station = byId[ranked.Id];
            return new RankedStation(
                station.Id, station.Name, station.Brand, station.Address, station.Latitude, station.Longitude,
                ranked.Candidate.PriceCents, Math.Round(ranked.Candidate.DistanceKm, 2),
                ranked.Candidate.DurationMinutes is { } minutes ? Math.Round(minutes, 1) : null, ranked.Cost, ranked.Saves,
                station.MembersOnly, ranked.OverDetourLimit, ranked.SavingUncertain);
        }

        return TypedResults.Ok(new BestNearby(
            ToRanked(ranking.Pick), ranking.Reference.Id, ranking.Reference.Id, ranking.Cheapest.Id,
            ranking.Listed.Select(ToRanked).ToList(), areaRadiusKm, ByRoad: byRoad is not null));
    }

    /// <summary>
    /// Best near you by road: the driver snapped to any road (an autoroute included), then one table from the driver to the
    /// stations and one back. Null, for the estimate instead, when the routing server fails, the driver is more than
    /// <see cref="NearYou.MaxDriverSnapMeters"/> from a road, or no station has a road there and back.
    /// </summary>
    private static async Task<NearbyRanking?> RankByRoadAsync(
        OsrmClient osrm,
        ILoggerFactory loggers,
        LatLon driver,
        IReadOnlyCollection<(SnapshotStation Station, decimal PriceCents, double Km)> area,
        Car car,
        double maxDetourMin,
        CancellationToken cancellationToken)
    {
        var measured = NearYou.SelectByRoad(area, s => s.Km, s => s.PriceCents);
        if (measured.Count == 0)
        {
            return null;
        }

        try
        {
            var snapped = await osrm.NearestAsync(driver, cancellationToken);
            if (snapped.SnapMeters > NearYou.MaxDriverSnapMeters)
            {
                return null;
            }

            List<LatLon> points = [driver, .. measured.Select(s => new LatLon(s.Station.Latitude, s.Station.Longitude))];
            List<string?> hints = [snapped.Hint, .. measured.Select(_ => (string?)null)];
            var stations = Enumerable.Range(1, measured.Count).ToList();
            var outbound = await osrm.TableAsync(points, hints, [0], stations, cancellationToken);
            var back = await osrm.TableAsync(points, hints, stations, [0], cancellationToken);
            var candidates = NearYou.ByRoad([.. measured.Select(s => (s.Station.Id, s.PriceCents))], outbound, back);
            return NearYou.Rank(candidates, car, maxDetourMin, byRoad: true);
        }
        catch (OsrmException ex)
        {
            loggers.CreateLogger(RoutingLog.Category).LogOsrmFailure(ex);
            return null;
        }
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
