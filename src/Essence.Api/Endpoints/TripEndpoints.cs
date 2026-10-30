using System.ComponentModel;
using Essence.Contracts;
using Essence.Data;
using Essence.Ranking;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api.Endpoints;

public static class TripEndpoints
{
    private const double MaxRangeKm = 2000;
    private const double MaxSpareKm = 200;

    public static IEndpointRouteBuilder MapTripEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/trips").WithTags("Trips").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/best", Best)
            .WithSummary("Where to fill up on the way to a destination")
            .WithDescription(
                "Routes from the start to the destination by car, then ranks the operating stations with an available price "
                + "near the route (within `maxDetourMin` × 0.5 km of it, between 1 and 8 km; at most 100, the closest and cheapest "
                + "of every 10 km) by what the fill-up costs once the gas its detour burns is counted. The reference is the "
                + "cheapest station on the way (a stop adds at most 1 km and 2 min), or else the smallest detour. The pick is the "
                + "lowest cost within `maxDetourMin`, if it saves at least $0.25. On trips of 100 km or more, `rangeKm` (what the "
                + "dashboard says is left) less `spareKm` says which stations can be reached and where to stop; when it reaches "
                + "none, the whole range counts, or else the nearest station. Québec only: a destination outside Québec's boundary, "
                + "a start outside its box, or either more than 1 km from a road is refused with 422, and so is a trip with no "
                + "route. 503 when the routing server is down.")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithDataVersion();
        return app;
    }

    private static async Task<Results<Ok<TripPlan>, ValidationProblem, ProblemHttpResult>> Best(
        EssenceDbContext db,
        StationSnapshot snapshot,
        OsrmClient osrm,
        DataServiceMonitor services,
        ILoggerFactory loggers,
        [Description("Latitude of the start, in degrees.")] double fromLat,
        [Description("Longitude of the start, in degrees.")] double fromLon,
        [Description("Latitude of the destination, in degrees.")] double toLat,
        [Description("Longitude of the destination, in degrees.")] double toLon,
        CancellationToken cancellationToken,
        [Description("Régulier, Super or Diesel (accents optional). Required.")] string? fuel = null,
        [Description("Litres bought at a stop.")] double fillL = 40,
        [Description("The car's consumption, in L/100 km.")] double per100Km = 8.5,
        [Description("true to include Costco, which sells to members only.")] bool costco = false,
        [Description("The longest detour, in minutes: a station whose detour takes longer is listed, not picked.")]
        double maxDetourMin = 10,
        [Description("The range the dashboard shows, in km. Counted on trips of 100 km or more.")] double? rangeKm = null,
        [Description("The km of range never counted on.")] double spareKm = 30)
    {
        var errors = new QueryErrors()
            .Require(fromLat is >= -90 and <= 90, nameof(fromLat), "Must be between -90 and 90.")
            .Require(fromLon is >= -180 and <= 180, nameof(fromLon), "Must be between -180 and 180.")
            .Require(toLat is >= -90 and <= 90, nameof(toLat), "Must be between -90 and 90.")
            .Require(toLon is >= -180 and <= 180, nameof(toLon), "Must be between -180 and 180.")
            .Require(!string.IsNullOrWhiteSpace(fuel), nameof(fuel), "Required.")
            .Require(fillL is > 0 and <= StationEndpoints.MaxFillLitres, nameof(fillL), $"Must be more than 0 and at most {StationEndpoints.MaxFillLitres}.")
            .Require(per100Km is > 0 and <= StationEndpoints.MaxLitresPer100Km, nameof(per100Km),
                $"Must be more than 0 and at most {StationEndpoints.MaxLitresPer100Km}.")
            .Require(maxDetourMin is >= 0 and <= StationEndpoints.MaxDetourMinutes, nameof(maxDetourMin),
                $"Must be between 0 and {StationEndpoints.MaxDetourMinutes}.")
            .Require(rangeKm is null or (>= 0 and <= MaxRangeKm), nameof(rangeKm), $"Must be between 0 and {MaxRangeKm}.")
            .Require(spareKm is >= 0 and <= MaxSpareKm, nameof(spareKm), $"Must be between 0 and {MaxSpareKm}.");
        var fuelType = await StationEndpoints.ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any || fuelType is null)
        {
            return errors.ToProblem();
        }

        // Points outside Québec are refused before OSRM, whose data is Québec's alone (with a few km past the border) and
        // would snap them to the nearest road there. The destination must be inside the province's boundary: Ottawa's
        // downtown or Derby Line, Vermont, are under 1 km from a Québec road. The start only inside its box, then near a
        // road: a driver just across the border still gets the stations on the way into Québec.
        var (from, to) = (new LatLon(fromLat, fromLon), new LatLon(toLat, toLon));
        if (!QuebecArea.InBox(from))
        {
            return NoRoad("from", "Outside Québec");
        }

        if (!QuebecArea.Contains(to))
        {
            return NoRoad("to", "Outside Québec");
        }

        // While the routing data is built or loaded, OSRM isn't asked (nor its circuit breaker tripped).
        if (await services.NotServingAsync(DataService.Routing, cancellationToken) is { } preparing)
        {
            return RoutingUnavailable(preparing);
        }

        var logger = loggers.CreateLogger(RoutingLog.Category);
        try
        {
            // 1. The route, its ends snapped to any road (an autoroute included).
            var route = await osrm.RouteAsync(from, to, cancellationToken);
            if (route.Waypoints[0].SnapMeters > OnTheWay.MaxEndSnapMeters)
            {
                return NoRoad("from", "No road near this point");
            }

            if (route.Waypoints[1].SnapMeters > OnTheWay.MaxEndSnapMeters)
            {
                return NoRoad("to", "No road near this point");
            }

            var tripKm = route.DistanceMeters / 1000;
            var line = new RouteLine(Polyline.Decode(route.Geometry), tripKm);

            // 2. The stations within the half-width of the route, in memory.
            var halfWidthKm = OnTheWay.HalfWidthKm(maxDetourMin);
            var fuelId = fuelType.Id;
            var byId = new Dictionary<long, SnapshotStation>();
            var near = new List<RouteStation>();
            foreach (var station in (await snapshot.GetAsync(db, cancellationToken)).Stations)
            {
                var price = station.Prices.FirstOrDefault(p => p.FuelTypeId == fuelId);
                if (price is null || !Eligibility.IsEligible(station.Status, price.IsAvailable, price.PriceCents, station.MembersOnly, costco)
                    || line.Locate(new LatLon(station.Latitude, station.Longitude), halfWidthKm) is not { } position)
                {
                    continue;
                }

                byId[station.Id] = station;
                near.Add(new RouteStation(station.Id, price.PriceCents!.Value, position.KmAlong, position.OffKm));
            }

            // 3. At most 100 candidates; past the range (the whole of it, for the low-fuel case), only the first ones.
            var range = rangeKm is { } km ? new FuelRange(km, spareKm) : null;
            var maxKmAlong = range is not null && tripKm >= OnTheWay.LongTripKm ? range.RangeKm + halfWidthKm : (double?)null;
            var candidates = OnTheWay.SelectCandidates(near, maxKmAlong);

            // 4. The stations on their own roads, then start → destination and → each station, and each station →
            // destination. On any road, like the route, so the ends snap where the route's did.
            TripRoads? roads = new(tripKm, route.DurationSeconds / 60, []);
            var snapped = await osrm.SnapAsync([.. candidates.Select(c => new LatLon(byId[c.Id].Latitude, byId[c.Id].Longitude))], cancellationToken);
            var onRoads = Snapping.OnTheirRoads(candidates, snapped);
            if (onRoads.Count > 0)
            {
                List<LatLon> points = [from, to, .. onRoads.Select(s => s.OnRoad)];
                var stations = Enumerable.Range(2, onRoads.Count).ToList();
                var fromStart = await osrm.TableAsync(points, [0], [1, .. stations], anyRoad: true, cancellationToken);
                var toDestination = await osrm.TableAsync(points, stations, [1], anyRoad: true, cancellationToken);
                roads = OnTheWay.ByRoad([.. onRoads.Select(s => s.Station)], fromStart, toDestination);
                if (roads is null)
                {
                    return NoRoad("to", "No route to this point");
                }
            }

            // 5–6. The range, then the ranking.
            var ranking = OnTheWay.Rank(roads, tripKm, new Car((decimal)fillL, (decimal)per100Km), maxDetourMin, range);
            var tripStations = ranking.Ranked
                .OrderBy(r => r.Candidate.KmAlong)
                .ThenBy(r => r.Id)
                .Select(r =>
                {
                    var station = byId[r.Id];
                    return new TripStation(
                        station.Id, station.Name, station.Brand, station.Address, station.Latitude, station.Longitude,
                        r.Candidate.PriceCents, Math.Round(r.Candidate.KmAlong, 1), Math.Round(r.Candidate.ToKm, 1),
                        Math.Round(r.DetourKm, 1), Math.Round(r.DetourMinutes, 1), r.Cost, r.Saves, station.MembersOnly, r.Reachable,
                        r.OverDetourLimit);
                })
                .ToList();

            return TypedResults.Ok(new TripPlan(
                [.. line.Simplified.Select(p => new[] { Math.Round(p.Longitude, 5), Math.Round(p.Latitude, 5) })],
                Math.Round(tripKm, 1), Math.Round(route.DurationSeconds / 60, 1), halfWidthKm, ranking.LongTrip,
                ranking.StopBeforeKm is { } stop ? Math.Round(stop, 1) : null, ranking.LowFuel, tripStations, ranking.Pick?.Id,
                ranking.Reference?.Id, ranking.ReferenceOnTheWay, ranking.BiggestSaving?.Id));
        }
        catch (OsrmException ex)
        {
            logger.LogOsrmFailure(ex);
            return ex.Failure switch
            {
                OsrmFailure.NoRoute => NoRoad("to", "No route to this point"),
                OsrmFailure.Unavailable => RoutingUnavailable(await services.RefusedAsync(DataService.Routing)),
                _ => TypedResults.Problem("The routing server refused the request.", statusCode: StatusCodes.Status500InternalServerError),
            };
        }
    }

    /// <summary>503, with the routing data's progress when it's being prepared.</summary>
    private static ProblemHttpResult RoutingUnavailable(DataStatus? status) =>
        DataServiceRegistration.Unavailable(
            DataService.Routing, status, "Routing is unavailable", "The routing server doesn't answer; try again in a few minutes.");

    /// <summary>422: no road near the start or the destination, or none between them.</summary>
    /// <param name="point"><c>from</c> or <c>to</c>, in the problem's <c>point</c>.</param>
    private static ProblemHttpResult NoRoad(string point, string title) =>
        TypedResults.Problem(
            point == "from" ? "The start can't be reached by road." : "The destination can't be reached by road.",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title,
            extensions: new Dictionary<string, object?> { ["point"] = point });
}
