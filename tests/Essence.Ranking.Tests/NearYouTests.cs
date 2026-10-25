namespace Essence.Ranking.Tests;

public class NearYouTests
{
    private const long Ultramar = 1;
    private const long Costco = 2;
    private static readonly Car Mazda3 = new(40, 8.5m);

    /// <summary>A station <paramref name="km"/> away by road, driven there and back at 40 km/h.</summary>
    private static Candidate ByRoad(long id, decimal price, double km) => new(id, price, km, 2 * km, 2 * km / 40 * 60);

    /// <summary>The mockup: the closest is an Ultramar at 0.8 km, a Costco 9.0 km away is 11 ¢/L cheaper.</summary>
    private static readonly Candidate[] Mockup = [ByRoad(Ultramar, 166.9m, 0.8), ByRoad(Costco, 155.9m, 9.0)];

    [Fact]
    public void Costco_saves_the_difference_of_the_costs()
    {
        var ranking = NearYou.Rank(Mockup, Mazda3, maxDetourMinutes: 30, byRoad: true)!;

        Assert.Equal(Costco, ranking.Pick.Id);
        Assert.Equal(64.75m, ranking.Pick.Cost);
        Assert.Equal(2.24m, ranking.Pick.Saves);
        Assert.Equal(Ultramar, ranking.Reference.Id);
        Assert.Equal(66.99m, ranking.Reference.Cost);
        Assert.Equal(0m, ranking.Reference.Saves);
        Assert.Equal(Costco, ranking.Cheapest.Id);
    }

    [Fact]
    public void A_station_past_the_longest_detour_is_listed_but_not_picked()
    {
        // Costco's round trip takes 27 min, the Ultramar's 2.4: 24.6 min more, over the default 10.
        var ranking = NearYou.Rank(Mockup, Mazda3, maxDetourMinutes: 10, byRoad: true)!;

        Assert.Equal(Ultramar, ranking.Pick.Id);
        var costco = Assert.Single(ranking.Listed, r => r.Id == Costco);
        Assert.True(costco.OverDetourLimit);
        Assert.Equal(2.24m, costco.Saves);
        Assert.Equal(Costco, ranking.Cheapest.Id);
    }

    [Fact]
    public void The_longest_detour_is_counted_against_the_closest_drive()
    {
        // 10 min more than the closest's 2.4 min is allowed; a hair more isn't.
        Candidate[] candidates = [ByRoad(Ultramar, 166.9m, 0.8), new(Costco, 155.9m, 4, 8, 12.4)];
        Assert.Equal(Costco, NearYou.Rank(candidates, Mazda3, 10, byRoad: true)!.Pick.Id);

        candidates[1] = new(Costco, 155.9m, 4, 8, 12.41);
        Assert.Equal(Ultramar, NearYou.Rank(candidates, Mazda3, 10, byRoad: true)!.Pick.Id);
    }

    [Fact]
    public void A_small_saving_is_about_the_same_and_the_closest_stays_the_pick()
    {
        // 1 ¢/L cheaper, 0.7 km farther: 1.659 × (40 + 3 × 0.085) = 66.78, 21 cents less.
        Candidate[] candidates = [ByRoad(Ultramar, 166.9m, 0.8), ByRoad(Costco, 165.9m, 1.5)];

        var ranking = NearYou.Rank(candidates, Mazda3, 10, byRoad: true)!;

        Assert.Equal(Ultramar, ranking.Pick.Id);
        Assert.Equal(Costco, ranking.Ranked[0].Id);
        Assert.Equal(0.21m, ranking.Ranked[0].Saves);
    }

    [Fact]
    public void A_cheaper_station_that_costs_more_to_reach_is_not_the_pick()
    {
        Candidate[] candidates = [ByRoad(Ultramar, 166.9m, 0.8), ByRoad(Costco, 166.4m, 12)];

        var ranking = NearYou.Rank(candidates, Mazda3, 60, byRoad: true)!;

        Assert.Equal(Ultramar, ranking.Pick.Id);
        Assert.Equal(Costco, ranking.Cheapest.Id);
        Assert.True(ranking.Cheapest.Saves < 0);
    }

    [Fact]
    public void Across_the_river_an_estimated_saving_must_hold_with_roads_twice_the_straight_line()
    {
        // 0.5 km to the closest, 4 km straight across the river to one 4 ¢/L cheaper: it saves $0.34 with roads 1.3 times the
        // straight line, but costs $0.34 more with roads twice as long.
        Candidate[] candidates = [NearYou.Estimate(Ultramar, 166.9m, 0.5), NearYou.Estimate(Costco, 162.9m, 4)];

        var ranking = NearYou.Rank(candidates, Mazda3, maxDetourMinutes: 60, byRoad: false)!;

        Assert.Equal(Ultramar, ranking.Pick.Id);
        var across = ranking.Ranked.Single(r => r.Id == Costco);
        Assert.Equal(0.34m, across.Saves);
        Assert.True(across.SavingUncertain);
        Assert.False(across.OverDetourLimit);
        Assert.Contains(across, ranking.Listed);
    }

    [Fact]
    public void An_estimated_saving_that_holds_with_roads_twice_as_long_is_picked()
    {
        Candidate[] candidates = [NearYou.Estimate(Ultramar, 166.9m, 0.5), NearYou.Estimate(Costco, 159.9m, 4)];

        var ranking = NearYou.Rank(candidates, Mazda3, maxDetourMinutes: 60, byRoad: false)!;

        Assert.Equal(Costco, ranking.Pick.Id);
        Assert.False(ranking.Pick.SavingUncertain);
    }

    [Fact]
    public void Road_distances_need_no_cautious_check()
    {
        Candidate[] candidates = [NearYou.Estimate(Ultramar, 166.9m, 0.5), NearYou.Estimate(Costco, 162.9m, 4)];

        var ranking = NearYou.Rank(candidates, Mazda3, maxDetourMinutes: 60, byRoad: true)!;

        Assert.Equal(Costco, ranking.Pick.Id);
        Assert.All(ranking.Ranked, r => Assert.False(r.SavingUncertain));
    }

    [Fact]
    public void An_estimate_is_the_straight_line_times_the_road_factor_there_and_back_at_40_kmh()
    {
        var candidate = NearYou.Estimate(7, 160m, 10);

        Assert.Equal(13, candidate.DistanceKm, 9);
        Assert.Equal(26, candidate.ExtraKm, 9);
        Assert.Equal(39, candidate.ExtraMinutes, 9);
    }

    [Fact]
    public void Ties_go_to_the_closer_then_the_cheaper_then_the_lower_id()
    {
        // 3 and 4 tie on cost, distance and price; 5 is as cheap, but closer, so it costs less.
        Candidate[] candidates = [ByRoad(9, 170m, 0.5), ByRoad(4, 150m, 3), ByRoad(3, 150m, 3), ByRoad(5, 150m, 2.5)];

        var ranking = NearYou.Rank(candidates, Mazda3, 60, byRoad: true)!;

        Assert.Equal([5L, 3, 4, 9], ranking.Ranked.Select(r => r.Id));
        Assert.Equal(5, ranking.Pick.Id);
        Assert.Equal(5, ranking.Cheapest.Id);
    }

    [Fact]
    public void The_closest_is_the_reference_even_when_it_is_the_most_expensive()
    {
        Candidate[] candidates = [ByRoad(1, 160m, 3), ByRoad(2, 175m, 0.2), ByRoad(3, 161m, 2)];

        var ranking = NearYou.Rank(candidates, Mazda3, 60, byRoad: true)!;

        Assert.Equal(2, ranking.Reference.Id);
        Assert.All(ranking.Ranked.Where(r => r.Id != 2), r => Assert.True(r.Saves > 0));
    }

    [Fact]
    public void Listed_keeps_the_pick_the_closest_the_cheapest_and_the_first_savers_of_each_kind()
    {
        // The closest at 0.5 km and 12 cheaper ones: 6 close enough to pick, 6 past the longest detour.
        var candidates = new List<Candidate> { ByRoad(100, 175m, 0.5) };
        candidates.AddRange(Enumerable.Range(1, 6).Select(i => ByRoad(i, 160m + i, 1 + 0.1 * i)));
        candidates.AddRange(Enumerable.Range(11, 6).Select(i => ByRoad(i, 140m + i, 20)));

        var ranking = NearYou.Rank(candidates, Mazda3, 10, byRoad: true)!;

        Assert.Equal(1, ranking.Pick.Id);
        Assert.Equal(11, ranking.Cheapest.Id);
        Assert.Equal([1L, 2, 3, 4, 5, 11, 12, 13, 14, 15, 100], ranking.Listed.Select(r => r.Id).Order());
        Assert.Equal(ranking.Listed.OrderBy(r => r.Cost), ranking.Listed);
    }

    [Fact]
    public void One_station_is_the_pick_the_reference_and_the_cheapest()
    {
        var ranking = NearYou.Rank([ByRoad(1, 160m, 30)], Mazda3, 10, byRoad: true)!;

        Assert.Equal(1, ranking.Pick.Id);
        Assert.Same(ranking.Reference, ranking.Pick);
        Assert.Same(ranking.Cheapest, ranking.Pick);
        Assert.Single(ranking.Listed);
    }

    [Fact]
    public void No_candidates_no_ranking() =>
        Assert.Null(NearYou.Rank([], Mazda3, 10, byRoad: true));
}
