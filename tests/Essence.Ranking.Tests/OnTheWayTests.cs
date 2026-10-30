namespace Essence.Ranking.Tests;

public class OnTheWayTests
{
    private const long Ultramar = 1;
    private const long Sonic = 2;
    private const long PetroCanada = 3;
    private const long Shell = 4;
    private const long Irving = 5;
    private const long Costco = 6;
    private const double TripKm = 250;
    private static readonly Car Mazda3 = new(40, 8.5m);

    /// <summary>
    /// The mockup's road trip, Montréal to Québec on the A-20: 250 km at 100 km/h, and the stations along it with their
    /// detours.
    /// </summary>
    private static readonly TripRoads RoadTrip = Roads(
        TripKm,
        Station(Ultramar, 164.9m, kmAlong: 55, detourKm: 0.4, detourMinutes: 0.5),
        Station(Sonic, 156.9m, kmAlong: 90, detourKm: 10, detourMinutes: 12),
        Station(PetroCanada, 159.9m, kmAlong: 105, detourKm: 4, detourMinutes: 5),
        Station(Shell, 164.4m, kmAlong: 125, detourKm: 0, detourMinutes: 0),
        Station(Irving, 157.9m, kmAlong: 160, detourKm: 0, detourMinutes: 0),
        Station(Costco, 154.9m, kmAlong: 220, detourKm: 1.5, detourMinutes: 2));

    /// <summary>A station <paramref name="kmAlong"/> along a <paramref name="tripKm"/> route at 100 km/h, and its detour.</summary>
    private static TripCandidate Station(
        long id, decimal price, double kmAlong, double detourKm, double detourMinutes, double tripKm = TripKm) =>
        new(id, price, kmAlong, kmAlong + detourKm / 2, kmAlong * 0.6 + detourMinutes / 2, tripKm - kmAlong + detourKm / 2,
            (tripKm - kmAlong) * 0.6 + detourMinutes / 2);

    private static TripRoads Roads(double tripKm, params TripCandidate[] stations) => new(tripKm, tripKm * 0.6, stations);

    private static RankedTripCandidate Find(TripRanking ranking, long id) => Assert.Single(ranking.Ranked, r => r.Id == id);

    private static OsrmWaypoint Waypoint(double snapMeters) => new(new LatLon(45.5, -73.5), snapMeters);

    [Fact]
    public void With_180_km_of_range_the_road_trip_stops_before_km_150_at_the_best_value_within_10_min()
    {
        var ranking = OnTheWay.Rank(RoadTrip, TripKm, Mazda3, maxDetourMinutes: 10, new FuelRange(180, 30));

        Assert.Equal(150, ranking.StopBeforeKm);
        Assert.False(ranking.LowFuel);
        Assert.True(ranking.LongTrip);

        // Shell is the cheapest that's on the way: 164.4 × 40 L = $65.76.
        Assert.Equal(Shell, ranking.Reference!.Id);
        Assert.True(ranking.ReferenceOnTheWay);
        Assert.Equal(65.76m, ranking.Reference.Cost);

        // Petro-Canada: 159.9 × (40 + 4 km × 0.085) = $64.50, 5 min off the way.
        var pick = ranking.Pick!;
        Assert.Equal(PetroCanada, pick.Id);
        Assert.Equal(64.50m, pick.Cost);
        Assert.Equal(1.26m, pick.Saves);
        Assert.Equal(4, pick.DetourKm, 6);
        Assert.Equal(5, pick.DetourMinutes, 6);
        Assert.True(pick.Candidate.KmAlong <= ranking.StopBeforeKm);

        // Sonic saves more, $1.67, but its detour takes 12 min.
        Assert.Equal(Sonic, ranking.BiggestSaving!.Id);
        Assert.Equal(1.67m, ranking.BiggestSaving.Saves);
        Assert.True(ranking.BiggestSaving.OverDetourLimit);

        // Irving and Costco are past the range: listed, never picked.
        Assert.False(Find(ranking, Irving).Reachable);
        Assert.False(Find(ranking, Costco).Reachable);
    }

    [Fact]
    public void Reachable_stations_come_first_lowest_cost_first()
    {
        var ranking = OnTheWay.Rank(RoadTrip, TripKm, Mazda3, 10, new FuelRange(180, 30));

        Assert.Equal([Sonic, PetroCanada, Shell, Ultramar, Costco, Irving], ranking.Ranked.Select(r => r.Id));
    }

    [Fact]
    public void A_range_that_covers_the_trip_needs_no_stop_but_still_picks_the_best_value()
    {
        var ranking = OnTheWay.Rank(RoadTrip, TripKm, Mazda3, 10, new FuelRange(400, 30));

        Assert.Null(ranking.StopBeforeKm);
        Assert.All(ranking.Ranked, r => Assert.True(r.Reachable));

        // Irving is the cheapest on the way now; Costco, 2 min off it, saves $1.00.
        Assert.Equal(Irving, ranking.Reference!.Id);
        Assert.Equal(Costco, ranking.Pick!.Id);
        Assert.Equal(62.16m, ranking.Pick.Cost);
        Assert.Equal(1.00m, ranking.Pick.Saves);
        Assert.Null(ranking.BiggestSaving);
    }

    [Fact]
    public void Without_a_range_every_station_is_reachable()
    {
        var ranking = OnTheWay.Rank(RoadTrip, TripKm, Mazda3, 10, range: null);

        Assert.Null(ranking.StopBeforeKm);
        Assert.False(ranking.LowFuel);
        Assert.All(ranking.Ranked, r => Assert.True(r.Reachable));
        Assert.Equal(Costco, ranking.Pick!.Id);
    }

    [Fact]
    public void A_short_trip_ignores_the_range()
    {
        var commute = Roads(14, Station(Shell, 163.9m, 1.5, 0, 0, 14), Station(PetroCanada, 160.9m, 6, 2.4, 4, 14));

        var ranking = OnTheWay.Rank(commute, 14, Mazda3, 10, new FuelRange(5, 30));

        Assert.False(ranking.LongTrip);
        Assert.False(ranking.LowFuel);
        Assert.Null(ranking.StopBeforeKm);
        Assert.Equal(PetroCanada, ranking.Pick!.Id);
        Assert.Equal(Shell, ranking.Reference!.Id);
    }

    [Fact]
    public void Low_fuel_counts_the_whole_range_when_the_spare_leaves_nothing()
    {
        // 60 km left, 30 spare: nothing within 30 km by road, but the Ultramar at km 55 is within 60.
        var ranking = OnTheWay.Rank(RoadTrip, TripKm, Mazda3, 10, new FuelRange(60, 30));

        Assert.True(ranking.LowFuel);
        Assert.Equal(0, ranking.StopBeforeKm);
        Assert.Equal([Ultramar], ranking.Ranked.Where(r => r.Reachable).Select(r => r.Id));
        Assert.Equal(Ultramar, ranking.Pick!.Id);
    }

    [Fact]
    public void Low_fuel_with_nothing_within_range_keeps_the_nearest_station()
    {
        var ranking = OnTheWay.Rank(RoadTrip, TripKm, Mazda3, 10, new FuelRange(20, 30));

        Assert.True(ranking.LowFuel);
        Assert.Equal(0, ranking.StopBeforeKm);
        Assert.Equal(Ultramar, Assert.Single(ranking.Ranked, r => r.Reachable).Id);
        Assert.Equal(Ultramar, ranking.Pick!.Id);
    }

    [Fact]
    public void With_no_station_on_the_way_the_smallest_detour_is_the_reference()
    {
        var roads = Roads(TripKm, Station(Sonic, 156.9m, 90, 10, 12), Station(PetroCanada, 159.9m, 105, 4, 5));

        var ranking = OnTheWay.Rank(roads, TripKm, Mazda3, 10, null);

        Assert.False(ranking.ReferenceOnTheWay);
        Assert.Equal(PetroCanada, ranking.Reference!.Id);
        Assert.Equal(PetroCanada, ranking.Pick!.Id);
        Assert.Equal(Sonic, ranking.BiggestSaving!.Id);
        Assert.False(ranking.Reference.OverDetourLimit);
    }

    [Fact]
    public void The_reference_is_never_over_the_detour_limit()
    {
        var roads = Roads(TripKm, Station(Sonic, 156.9m, 90, 20, 25));

        var ranking = OnTheWay.Rank(roads, TripKm, Mazda3, 10, null);

        Assert.Equal(Sonic, ranking.Pick!.Id);
        Assert.False(ranking.Pick.OverDetourLimit);
        Assert.Null(ranking.BiggestSaving);
    }

    [Fact]
    public void A_small_saving_leaves_the_reference_as_the_pick()
    {
        // 0.5 ¢/L less for 3 km more: 1.639 × 40.255 = $65.98, against $65.76.
        var roads = Roads(TripKm, Station(Shell, 164.4m, 125, 0, 0), Station(PetroCanada, 163.9m, 105, 3, 4));

        var ranking = OnTheWay.Rank(roads, TripKm, Mazda3, 10, null);

        Assert.Equal(Shell, ranking.Pick!.Id);
        Assert.Null(ranking.BiggestSaving);
    }

    [Fact]
    public void A_detour_is_never_negative()
    {
        // The station's legs are shorter than the direct road as OSRM measured it (different snapping).
        var roads = new TripRoads(100, 60, [new TripCandidate(Shell, 164.4m, 50, 49.5, 30.2, 50.2, 30)]);

        var ranking = OnTheWay.Rank(roads, 100, Mazda3, 10, null);

        Assert.Equal(0, ranking.Pick!.DetourKm);
        Assert.Equal(0.2, ranking.Pick.DetourMinutes, 6);
    }

    [Fact]
    public void Ties_go_to_the_shorter_detour_then_the_price_then_the_id()
    {
        var roads = Roads(TripKm, Station(9, 160m, 50, 0, 1.5), Station(8, 160m, 60, 0, 1), Station(7, 160m, 70, 0, 1));

        var ranking = OnTheWay.Rank(roads, TripKm, Mazda3, 10, null);

        Assert.Equal([7, 8, 9], ranking.Ranked.Select(r => r.Id));
        Assert.Equal(7, ranking.Reference!.Id);
    }

    [Fact]
    public void Nothing_to_rank_has_no_pick()
    {
        var ranking = OnTheWay.Rank(Roads(TripKm), TripKm, Mazda3, 10, new FuelRange(180, 30));

        Assert.Empty(ranking.Ranked);
        Assert.Null(ranking.Reference);
        Assert.Null(ranking.Pick);
        Assert.False(ranking.LowFuel);
        Assert.Null(ranking.StopBeforeKm);
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 8)]
    public void The_half_width_follows_the_longest_detour(double maxDetourMinutes, double halfWidthKm) =>
        Assert.Equal(halfWidthKm, OnTheWay.HalfWidthKm(maxDetourMinutes));

    [Fact]
    public void Candidates_cover_every_stretch_of_a_long_route_before_the_cheapest_of_the_rest()
    {
        // 300 km with 6 stations every 10 km: 180, of which 100 are measured.
        var stations = Enumerable.Range(0, 180)
            .Select(i => new RouteStation(i, 150m + i % 6, KmAlong: i / 6 * 10 + i % 6, OffKm: (5 - i % 6) * 0.5))
            .ToList();

        var candidates = OnTheWay.SelectCandidates(stations, maxKmAlong: null);

        Assert.Equal(OnTheWay.MaxCandidates, candidates.Count);
        Assert.Equal(candidates.OrderBy(c => c.KmAlong), candidates);
        foreach (var bin in stations.GroupBy(s => (int)(s.KmAlong / 10)))
        {
            var chosen = candidates.Where(c => (int)(c.KmAlong / 10) == bin.Key).ToList();
            Assert.Contains(chosen, c => c.Id == bin.MinBy(s => s.OffKm)!.Id);
            Assert.Contains(chosen, c => c.Id == bin.MinBy(s => s.PriceCents)!.Id);
        }
    }

    [Fact]
    public void A_short_route_measures_every_station()
    {
        // One stretch of 8: the closest 0 and 1, the cheapest 7, 6 and 5, then the rest fill in.
        var stations = Enumerable.Range(0, 8).Select(i => new RouteStation(i, 160m - i, 1 + i * 0.5, 0.2 + i * 0.5)).ToList();

        var candidates = OnTheWay.SelectCandidates(stations, null);

        Assert.Equal(Enumerable.Range(0, 8).Select(i => (long)i), candidates.Select(c => c.Id));
    }

    [Fact]
    public void Candidates_past_the_range_are_dropped_except_the_first_ones()
    {
        var stations = Enumerable.Range(0, 20).Select(i => new RouteStation(i, 160m, KmAlong: 40 + i * 10, OffKm: 1)).ToList();

        var candidates = OnTheWay.SelectCandidates(stations, maxKmAlong: 105);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6], candidates.Select(c => c.Id));

        // Nothing within: the first ones stay, so a car low on fuel still has the nearest.
        Assert.Equal(OnTheWay.FirstAlwaysKept, OnTheWay.SelectCandidates(stations, maxKmAlong: 10).Count);
    }

    [Fact]
    public void Road_distances_come_from_the_two_tables()
    {
        RouteStation[] stations = [new(Shell, 164.4m, 125, 0.1), new(Sonic, 156.9m, 90, 4), new(Costco, 154.9m, 220, 1)];
        var fromStart = new OsrmTable(
            [[250_000, 125_100, 95_000, null]],
            [[9_000, 4_510, 3_600, null]],
            [Waypoint(3)],
            [Waypoint(12), Waypoint(0), Waypoint(0), Waypoint(0)]);
        var toDestination = new OsrmTable(
            [[124_900], [null], [29_500]],
            [[4_490], [3_000], [1_100]],
            [Waypoint(0), Waypoint(0), Waypoint(0)],
            [Waypoint(12)]);

        var roads = OnTheWay.ByRoad(stations, fromStart, toDestination)!;

        Assert.Equal(250, roads.DirectKm);
        Assert.Equal(150, roads.DirectMinutes);

        // Sonic has no road on to the destination, and Costco none from the start.
        var shell = Assert.Single(roads.Candidates);
        Assert.Equal(new TripCandidate(Shell, 164.4m, 125, 125.1, 4_510 / 60.0, 124.9, 4_490 / 60.0), shell);
    }

    [Fact]
    public void No_road_to_the_destination_has_no_road_distances()
    {
        var fromStart = new OsrmTable([[null, 1_000]], [[null, 60]], [Waypoint(3)], [Waypoint(5), Waypoint(5)]);
        var toDestination = new OsrmTable([[null]], [[null]], [Waypoint(5)], [Waypoint(5)]);

        Assert.Null(OnTheWay.ByRoad([new(Shell, 164.4m, 1, 0.1)], fromStart, toDestination));
    }
}
