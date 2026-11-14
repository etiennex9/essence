using System.ComponentModel;
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

    internal const string HoursDescription =
        "`hours` is the station's opening hours, null when no source has them: `rule` in OpenStreetMap's `opening_hours` "
        + "(`Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30`, `24/7`), in the station's time zone, and its `source`. Costco's own hours "
        + "(`costco`) beat the hours Essence checked (`curated`), which beat Ultramar's (`ultramar`) and Shell's (`shell`) "
        + "forecourts', then Couche-Tard's stores' (`circlek`), then OpenStreetMap's (`osm`, confirmed first; its data is "
        + "© OpenStreetMap contributors, ODbL, and `link` is the element); a source without a success in 21 days comes after "
        + "the others and isn't `trusted`, nor is OpenStreetMap.";

    /// <summary>How Best near you and Best on the way leave out stations closed on arrival (TripEndpoints' too).</summary>
    internal const string LeftOutDescription =
        "Before ranking, a station whose hours are `trusted` and either say it's closed on arrival (now plus `etaMin`, or "
        + "`toMin` on a trip; not on a holiday whose hours the rule doesn't state) or say it's `temporarilyClosed` is left out, "
        + "unless its id is in `ignoreHours`; other hours never leave a station out, and when leaving out would leave none, all "
        + "are kept. Stations likely closed then don't count among the closest measured by road. `leftOut` lists at most 2 "
        + "of those left out, only those cheaper per litre than the pick. No 304 once the data is more than 10 minutes old: "
        + "the answer depends on the time.";

    internal const string IgnoreHoursDescription =
        "Ids of stations whose hours the device corrected, separated by commas (at most 50): never left out for their hours.";

    /// <summary>Where every answer puts a station (README → Position corrections).</summary>
    internal const string PositionDescription =
        "A station's `latitude` and `longitude` are where it is: the Régie's point, or a reviewed correction's when the Régie's "
        + "is known to be off (`/stations/positions`); distances are from there.";

    private static readonly Expression<Func<Station, StationSummary>> ToSummary = s => new StationSummary(
        s.Id, s.Name, s.Brand, s.Status, s.Address, s.PostalCode, s.Region, s.Latitude, s.Longitude, s.IsActive);

    public static IEndpointRouteBuilder MapStationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stations").WithTags("Stations").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/", ListStations)
            .WithSummary("List stations")
            .WithDescription(
                "Filters combine with AND. `q` searches the name, and the address and postal code ignoring case and accents. "
                + PositionDescription);
        group.MapGet("/nearby", Nearby)
            .WithSummary("Active stations near a point, closest first")
            .WithDescription(PositionDescription + " " + HoursDescription)
            .WithDataVersion();
        group.MapGet("/best", Best)
            .WithSummary("Where to fill up near a point")
            .WithDescription(
                "Ranks the operating stations with an available price by what the fill-up costs once the gas burned getting there "
                + "and back is counted. The closest is the reference the savings are counted against. A farther station is picked "
                + "only if it saves at least $0.25 and its round trip takes at most `maxDetourMin` longer than the closest's; the "
                + "others stay listed, with the reason. Distances are by road (the 10 nearest and every station cheaper than one of "
                + "them, at most 200). When the routing server is down, or the point is more than 1 km from a road, they're "
                + "estimated instead (`byRoad` false): straight line × 1.3, driven at 40 km/h, and a farther station must also "
                + "save with roads twice the straight line. Looks within `radiusKm`, widened to 25 km with fewer than 5 stations, "
                + "else returns the closest at any distance. " + LeftOutDescription + " " + PositionDescription + " " + HoursDescription)
            .WithDataVersion(clockDependent: true);
        group.MapGet("/{id:long}", GetStation)
            .WithSummary("A station with its current prices and opening hours")
            .WithDescription(
                PositionDescription + " " + HoursDescription + " `osmElement` is the OpenStreetMap element the station was matched to, its hours or not "
                + "(`node/123`, `way/456`, `relation/789`): where the app sends a correction of them. Null when none was. "
                + "`osmBrandDiffers` is true when that element was matched by place only, being of another brand or none on "
                + "OpenStreetMap, so the app offers to set the station's brand on it too; false otherwise, null without an "
                + "element. `osmBrand` is the element's brand there as Essence last read it (its `brand`, else `operator`, else "
                + "`name`: `Sonic`), when a feed station has that brand; null for none (a name of its own is no brand), or "
                + "without an element. An element with no `brand` tag whose name is that brand is named after it.")
            .WithDataVersion();
        group.MapGet("/{id:long}/prices", GetPriceHistory)
            .WithSummary("Price history of a station")
            .WithDescription("Every price state that overlaps [from, to). Defaults to the last 30 days.");
        return app;
    }

    private static async Task<Results<Ok<PagedList<StationSummary>>, ValidationProblem>> ListStations(
        EssenceDbContext db,
        StationSnapshot snapshot,
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
        var current = await snapshot.GetAsync(db, cancellationToken);
        return TypedResults.Ok(new PagedList<StationSummary>([.. items.Select(s => Located(s, current))], page, pageSize, total));
    }

    private static async Task<Results<Ok<StationDetail>, NotFound>> GetStation(
        EssenceDbContext db,
        StationSnapshot snapshot,
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
                    .ToList(),
                null))
            .FirstOrDefaultAsync(cancellationToken);
        if (station is null)
        {
            return TypedResults.NotFound();
        }

        var current = await snapshot.GetAsync(db, cancellationToken);
        return TypedResults.Ok(WithSnapshot(station, current));
    }

    /// <summary>A station of the list where it is: a correction's point when one applies.</summary>
    private static StationSummary Located(StationSummary station, Snapshot current)
    {
        var (latitude, longitude) = current.PointOf(station.Id, station.Latitude, station.Longitude);
        return station with { Latitude = latitude, Longitude = longitude };
    }

    /// <summary>
    /// A station's details with what the snapshot knows of it: where it is (a correction's point when one applies), its
    /// hours, and its OpenStreetMap element.
    /// </summary>
    public static StationDetail WithSnapshot(StationDetail station, Snapshot current)
    {
        var element = current.OsmElementOf(station.Id);
        var (latitude, longitude) = current.PointOf(station.Id, station.Latitude, station.Longitude);
        return station with
        {
            Latitude = latitude,
            Longitude = longitude,
            Hours = current.Hours.GetValueOrDefault(station.Id)?.Dto,
            OsmElement = element?.Ref,
            OsmBrandDiffers = element?.BrandDiffers,
            OsmBrand = element?.Brand,
        };
    }

    private static async Task<Results<Ok<List<NearbyStation>>, ValidationProblem>> Nearby(
        EssenceDbContext db,
        StationSnapshot snapshot,
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

        // The closest in the snapshot, where each station is (a correction's point when one applies): the database keeps
        // the feed's points. Fuel type ids start at 1, so 0 means "any fuel".
        short fuelId = fuelType?.Id ?? 0;
        var current = await snapshot.GetAsync(db, cancellationToken);
        var hits = Closest(current.Stations, lat, lon, radiusKm, fuelId == 0 ? null : fuelId, limit);

        var ids = hits.Select(h => h.Station.Id).ToList();
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
                Prices = s.CurrentPrices
                    .Where(p => fuelId == 0 || p.FuelTypeId == fuelId)
                    .OrderBy(p => p.FuelTypeId)
                    .Select(p => new CurrentPriceDto(p.FuelType.Code, p.PriceCents, p.IsAvailable, p.Since))
                    .ToList(),
            })
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var result = hits
            .Where(h => stations.ContainsKey(h.Station.Id))
            .Select(h =>
            {
                var s = stations[h.Station.Id];
                return new NearbyStation(
                    s.Id, s.Name, s.Brand, s.Address, s.PostalCode, s.Region, h.Station.Latitude, h.Station.Longitude,
                    Math.Round(h.DistanceKm, 3), s.Prices, current.Hours.GetValueOrDefault(s.Id)?.Dto);
            })
            .ToList();
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// The snapshot's stations within <paramref name="radiusKm"/> of a point, the closest <paramref name="limit"/>, closest
    /// first, with how far each is; only those with a price row for the fuel, when one is given, available or not.
    /// </summary>
    public static List<(SnapshotStation Station, double DistanceKm)> Closest(
        IReadOnlyList<SnapshotStation> stations, double latitude, double longitude, double radiusKm, short? fuelId, int limit) =>
        [.. stations
            .Where(s => fuelId is not { } fuel || s.Prices.Any(p => p.FuelTypeId == fuel))
            .Select(s => (Station: s, DistanceKm: Geo.DistanceKm(latitude, longitude, s.Latitude, s.Longitude)))
            .Where(s => s.DistanceKm <= radiusKm)
            .OrderBy(s => s.DistanceKm)
            .ThenBy(s => s.Station.Id)
            .Take(limit)];

    private static async Task<Results<Ok<BestNearby>, ValidationProblem>> Best(
        EssenceDbContext db,
        StationSnapshot snapshot,
        OsrmClient osrm,
        DataServiceMonitor services,
        TimeProvider time,
        ILoggerFactory loggers,
        HttpContext http,
        [Description("Latitude of the driver, in degrees.")] double lat,
        [Description("Longitude of the driver, in degrees.")] double lon,
        CancellationToken cancellationToken,
        [Description("Régulier, Super or Diesel (accents optional). Required.")] string? fuel = null,
        [Description("Litres bought at a stop.")] double fillL = 40,
        [Description("The car's consumption, in L/100 km.")] double per100Km = 8.5,
        [Description("true to include Costco, which sells to members only.")] bool costco = false,
        [Description("The longest detour: how many minutes longer than the closest's a station's round trip may take to be picked.")]
        double maxDetourMin = 10,
        [Description("Search radius in kilometres (max 25).")] double radiusKm = SearchArea.DefaultRadiusKm,
        [Description(IgnoreHoursDescription)] string? ignoreHours = null)
    {
        var ignored = IgnoredHours.Parse(ignoreHours);
        var errors = new QueryErrors()
            .Require(lat is >= -90 and <= 90, nameof(lat), "Must be between -90 and 90.")
            .Require(lon is >= -180 and <= 180, nameof(lon), "Must be between -180 and 180.")
            .Require(!string.IsNullOrWhiteSpace(fuel), nameof(fuel), "Required.")
            .Require(fillL is > 0 and <= MaxFillLitres, nameof(fillL), $"Must be more than 0 and at most {MaxFillLitres}.")
            .Require(per100Km is > 0 and <= MaxLitresPer100Km, nameof(per100Km), $"Must be more than 0 and at most {MaxLitresPer100Km}.")
            .Require(maxDetourMin is >= 0 and <= MaxDetourMinutes, nameof(maxDetourMin), $"Must be between 0 and {MaxDetourMinutes}.")
            .Require(radiusKm is > 0 and <= SearchArea.MaxRadiusKm, nameof(radiusKm), $"Must be more than 0 and at most {SearchArea.MaxRadiusKm}.")
            .Require(ignored is not null, nameof(ignoreHours), IgnoredHours.Error);
        var fuelType = await ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any || fuelType is null || ignored is null)
        {
            return errors.ToProblem();
        }

        var current = await snapshot.GetAsync(db, cancellationToken);
        var stations = Eligible(current.Stations, fuelType.Id, costco, lat, lon);
        var (area, areaRadiusKm) = SearchArea.Select(stations, s => s.Km, radiusKm);

        // By road when the routing server answers; road and estimated figures are never mixed in one ranking.
        // Stations closed when the driver gets there are left out first: the closest the savings are counted against is
        // then one that's open. Those likely closed at the estimate's arrival don't count among the nearest measured by
        // road, so the open ones beyond them are measured too.
        var now = time.GetUtcNow();
        bool LikelyLeftOut((SnapshotStation Station, decimal PriceCents, double Km) s) =>
            !ignored.Contains(s.Station.Id)
            && Arrivals.IsClosed(
                current.Hours.GetValueOrDefault(s.Station.Id)?.Arrival,
                now.AddMinutes(EtaMinutes(NearYou.Estimate(s.Station.Id, s.PriceCents, s.Km))));

        var car = new Car((decimal)fillL, (decimal)per100Km);
        var roadCandidates = await services.NotServingAsync(DataService.Routing, cancellationToken) is null
            ? await CandidatesByRoadAsync(osrm, services, loggers, http, new LatLon(lat, lon), area, LikelyLeftOut, cancellationToken)
            : Estimated(http);
        var candidates = roadCandidates ?? [.. area.Select(s => NearYou.Estimate(s.Station.Id, s.PriceCents, s.Km))];

        var (kept, leftOut) = Arrivals.LeaveOutClosed(
            candidates, c => current.Hours.GetValueOrDefault(c.Id)?.Arrival, c => now.AddMinutes(EtaMinutes(c)), ignored, c => c.Id);
        var ranking = NearYou.Rank(kept, car, maxDetourMin, byRoad: roadCandidates is not null);
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
                station.MembersOnly, ranked.OverDetourLimit, ranked.SavingUncertain, current.Hours.GetValueOrDefault(station.Id)?.Dto,
                Math.Round(EtaMinutes(ranked.Candidate), 1));
        }

        var pick = ToRanked(ranking.Pick);
        return TypedResults.Ok(new BestNearby(
            pick, ranking.Reference.Id, ranking.Reference.Id, ranking.Cheapest.Id,
            ranking.Listed.Select(ToRanked).ToList(), areaRadiusKm, ByRoad: roadCandidates is not null,
            LeftOutStations.Of(
                leftOut.Select(c => new LeftOutCandidate(byId[c.Id], c.PriceCents, Costs.Fill(c.PriceCents, car, c.ExtraKm), EtaMinutes(c))),
                current.Hours,
                pick.PriceCents)));
    }

    /// <summary>
    /// The snapshot's stations Best near you may rank for the fuel, each with its price and how far it is from the driver
    /// in a straight line: from where the station is, a correction's point when one applies.
    /// </summary>
    public static List<(SnapshotStation Station, decimal PriceCents, double Km)> Eligible(
        IReadOnlyList<SnapshotStation> stations, short fuelId, bool costco, double latitude, double longitude) =>
        [.. stations
            .Select(s => (Station: s, Price: s.Prices.FirstOrDefault(p => p.FuelTypeId == fuelId)))
            .Where(x => x.Price is { } price
                && Eligibility.IsEligible(x.Station.Status, price.IsAvailable, price.PriceCents, x.Station.MembersOnly, costco))
            .Select(x => (
                x.Station,
                PriceCents: x.Price!.PriceCents!.Value,
                Km: Geo.DistanceKm(latitude, longitude, x.Station.Latitude, x.Station.Longitude)))];

    /// <summary>How long getting to the station takes, one way: by road, or the estimate's (half its round trip).</summary>
    private static double EtaMinutes(Candidate candidate) => candidate.DurationMinutes ?? candidate.ExtraMinutes / 2;

    /// <summary>The routing data is being built or loaded: the estimate, not cached, as when the server is down.</summary>
    private static List<Candidate>? Estimated(HttpContext http)
    {
        DataVersion.DontCache(http);
        return null;
    }

    /// <summary>
    /// Best near you's candidates by road: the stations snapped to their own roads, then one table from the driver to them
    /// and one back, the driver on any road (an autoroute included). Null, for the estimate instead, when the routing server
    /// fails, the driver is more than <see cref="NearYou.MaxDriverSnapMeters"/> from a road, or no station has a road there
    /// and back. An estimate the server's failure forced isn't cached: the same request asks again. A server that doesn't
    /// answer may have just stopped for new data: its status is asked again, so the next requests don't call it meanwhile.
    /// </summary>
    private static async Task<List<Candidate>?> CandidatesByRoadAsync(
        OsrmClient osrm,
        DataServiceMonitor services,
        ILoggerFactory loggers,
        HttpContext http,
        LatLon driver,
        IReadOnlyCollection<(SnapshotStation Station, decimal PriceCents, double Km)> area,
        Func<(SnapshotStation Station, decimal PriceCents, double Km), bool> likelyLeftOut,
        CancellationToken cancellationToken)
    {
        var measured = NearYou.SelectByRoad(area, s => s.Km, s => s.PriceCents, likelyLeftOut);
        if (measured.Count == 0)
        {
            return null;
        }

        try
        {
            var snapped = await osrm.SnapAsync([.. measured.Select(s => new LatLon(s.Station.Latitude, s.Station.Longitude))], cancellationToken);
            var onRoads = Snapping.OnTheirRoads(measured, snapped);
            if (onRoads.Count == 0)
            {
                return null;
            }

            List<LatLon> points = [driver, .. onRoads.Select(s => s.OnRoad)];
            var stations = Enumerable.Range(1, onRoads.Count).ToList();
            var outbound = await osrm.TableAsync(points, [0], stations, anyRoad: true, cancellationToken);
            if (outbound.Sources[0].SnapMeters > NearYou.MaxDriverSnapMeters)
            {
                return null;
            }

            var back = await osrm.TableAsync(points, stations, [0], anyRoad: true, cancellationToken);
            var candidates = NearYou.ByRoad([.. onRoads.Select(s => (s.Station.Station.Id, s.Station.PriceCents))], outbound, back);
            return candidates.Count > 0 ? candidates : null;
        }
        catch (OsrmException ex)
        {
            loggers.CreateLogger(RoutingLog.Category).LogOsrmFailure(ex);
            if (ex.Failure == OsrmFailure.Unavailable)
            {
                _ = services.RefusedAsync(DataService.Routing);
            }

            DataVersion.DontCache(http);
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
