using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class StationChoiceTests
{
    [Fact]
    public void A_selection_is_kept_when_the_same_question_still_lists_the_station() =>
        Assert.Equal(5, StationChoice.Keep(5, sameQuestion: true, pickId: 1, [1, 5, 7]));

    [Fact]
    public void A_selection_falls_back_to_the_pick_when_the_question_changed() =>
        Assert.Null(StationChoice.Keep(5, sameQuestion: false, pickId: 1, [1, 5, 7]));

    [Fact]
    public void A_selection_falls_back_to_the_pick_when_the_station_left_the_list() =>
        Assert.Null(StationChoice.Keep(5, sameQuestion: true, pickId: 1, [1, 7]));

    [Fact]
    public void Selecting_the_pick_is_no_selection()
    {
        Assert.Null(StationChoice.Keep(1, sameQuestion: true, pickId: 1, [1, 5]));
        Assert.Null(StationChoice.Keep(null, sameQuestion: true, pickId: 1, [1, 5]));
    }

    [Fact]
    public void A_trip_with_nothing_reachable_still_lets_a_station_be_selected() =>
        Assert.Equal(5, StationChoice.Keep(5, sameQuestion: true, pickId: null, [5, 7]));

    [Fact]
    public void Near_you_lists_the_tiles_and_three_others_that_save()
    {
        var result = Nearby();

        // Not 6 (fourth of the others), nor 8 (saves under $0.25).
        Assert.Equal([1, 2, 3, 4, 5, 7], StationChoice.Selectable(result).Order());
    }

    [Fact]
    public void Near_you_with_one_station_lists_only_the_pick()
    {
        var pick = Ranked(1, saves: 0);

        Assert.Equal([1], StationChoice.Selectable(new BestNearby(pick, 1, 1, 1, [pick], 10, true)));
    }

    [Fact]
    public void Near_you_without_a_pick_lists_nothing() =>
        Assert.Empty(StationChoice.Selectable(new BestNearby(null, null, null, null, [], 10, true)));

    [Fact]
    public void Others_put_the_stations_that_could_be_picked_first()
    {
        var result = Nearby();

        Assert.Equal([4, 5, 7], StationChoice.Others(result, result.Pick!).Select(s => s.Id));
    }

    [Fact]
    public void A_trip_lists_every_station_even_those_past_the_range()
    {
        var plan = Plan(null, null, OnTheWay(1, saves: 0, reachable: false), OnTheWay(2, saves: 1, reachable: false));

        Assert.Equal([1, 2], StationChoice.Selectable(plan));
    }

    [Fact]
    public void The_pick_that_isnt_the_closest_saves()
    {
        var result = Nearby();

        Assert.Equal(HeroCase.Saves, StationChoice.CaseOf(result.Pick!, result));
    }

    [Fact]
    public void The_closest_when_another_is_picked_is_the_reference()
    {
        var result = Nearby();

        Assert.Equal(HeroCase.Reference, StationChoice.CaseOf(result.Stations.Single(s => s.Id == 2), result));
    }

    [Fact]
    public void A_pick_beyond_the_widest_radius_says_nothing_more()
    {
        var pick = Ranked(1, saves: 0);

        Assert.Equal(HeroCase.PickBeyondRadius, StationChoice.CaseOf(pick, new BestNearby(pick, 1, 1, 1, [pick], null, true)));
    }

    [Theory]
    [InlineData(true, false, HeroCase.PickIsReferenceDetour)]
    [InlineData(false, true, HeroCase.PickIsReferenceUncertain)]
    public void The_closest_picked_says_why_those_that_save_more_arent(bool overDetour, bool uncertain, HeroCase expected)
    {
        var pick = Ranked(1, saves: 0);
        var blocked = Ranked(2, saves: 1.5m, overDetour: overDetour, uncertain: uncertain);

        Assert.Equal(expected, StationChoice.CaseOf(pick, new BestNearby(pick, 1, 1, 2, [pick, blocked], 10, true)));
    }

    [Fact]
    public void The_closest_picked_alone_is_the_only_one()
    {
        var pick = Ranked(1, saves: 0);

        Assert.Equal(HeroCase.PickIsReferenceOnly, StationChoice.CaseOf(pick, new BestNearby(pick, 1, 1, 1, [pick], 10, true)));
    }

    [Fact]
    public void The_closest_picked_when_nothing_saves_enough()
    {
        var pick = Ranked(1, saves: 0);
        var other = Ranked(2, saves: 0.24m);

        Assert.Equal(HeroCase.PickIsReferenceNothingSaves, StationChoice.CaseOf(pick, new BestNearby(pick, 1, 1, 2, [pick, other], 10, true)));
    }

    [Theory]
    [InlineData(0.25, false, false, HeroCase.Saves)]
    [InlineData(0.25, true, false, HeroCase.SavesOverDetour)]
    [InlineData(0.25, false, true, HeroCase.SavesUncertain)]
    [InlineData(1.0, true, true, HeroCase.SavesOverDetour)]
    [InlineData(0.24, false, false, HeroCase.AboutTheSame)]
    [InlineData(0.24, true, true, HeroCase.AboutTheSame)]
    [InlineData(0.0, false, false, HeroCase.AboutTheSame)]
    [InlineData(-0.24, false, false, HeroCase.AboutTheSame)]
    [InlineData(-0.25, false, false, HeroCase.CostsMore)]
    public void Another_station_near_you_is_told_by_its_saving(double saves, bool overDetour, bool uncertain, HeroCase expected)
    {
        var result = Nearby();
        var station = Ranked(9, saves: (decimal)saves, overDetour: overDetour, uncertain: uncertain);

        Assert.Equal(expected, StationChoice.CaseOf(station, result with { Stations = [.. result.Stations, station] }));
    }

    [Fact]
    public void A_station_past_the_range_is_past_the_range_even_if_it_saves()
    {
        var plan = Plan(1, 2, OnTheWay(1, saves: 1), OnTheWay(2, saves: 0), OnTheWay(3, saves: 3, reachable: false));

        Assert.Equal(HeroCase.PastRange, StationChoice.CaseOf(plan.Stations[2], plan));
    }

    [Fact]
    public void The_pick_on_the_way_that_isnt_the_reference_saves()
    {
        var plan = Plan(1, 2, OnTheWay(1, saves: 1), OnTheWay(2, saves: 0));

        Assert.Equal(HeroCase.Saves, StationChoice.CaseOf(plan.Stations[0], plan));
        Assert.Equal(HeroCase.Reference, StationChoice.CaseOf(plan.Stations[1], plan));
    }

    [Fact]
    public void The_reference_picked_on_the_way_says_why()
    {
        var detour = Plan(1, 1, OnTheWay(1, saves: 0), OnTheWay(2, saves: 2, overDetour: true));
        var nothing = Plan(1, 1, OnTheWay(1, saves: 0), OnTheWay(2, saves: 0.24m), OnTheWay(3, saves: 2, reachable: false));
        var only = Plan(1, 1, OnTheWay(1, saves: 0), OnTheWay(2, saves: 2, reachable: false));

        Assert.Equal(HeroCase.PickIsReferenceDetour, StationChoice.CaseOf(detour.Stations[0], detour));
        Assert.Equal(HeroCase.PickIsReferenceNothingSaves, StationChoice.CaseOf(nothing.Stations[0], nothing));
        Assert.Equal(HeroCase.PickIsReferenceOnly, StationChoice.CaseOf(only.Stations[0], only));
    }

    [Theory]
    [InlineData(0.25, false, HeroCase.Saves)]
    [InlineData(0.25, true, HeroCase.SavesOverDetour)]
    [InlineData(0.24, true, HeroCase.AboutTheSame)]
    [InlineData(-0.24, false, HeroCase.AboutTheSame)]
    [InlineData(-0.25, false, HeroCase.CostsMore)]
    public void Another_station_on_the_way_is_told_by_its_saving(double saves, bool overDetour, HeroCase expected)
    {
        var plan = Plan(1, 2, OnTheWay(1, saves: 1), OnTheWay(2, saves: 0), OnTheWay(3, saves: (decimal)saves, overDetour: overDetour));

        Assert.Equal(expected, StationChoice.CaseOf(plan.Stations[2], plan));
    }

    /// <summary>
    /// Picked 1, closest 2, cheapest 3 (it saves little: it's far), then four that save, one of them past the longest
    /// detour, and 8, which saves under $0.25.
    /// </summary>
    private static BestNearby Nearby()
    {
        RankedStation[] stations =
        [
            Ranked(1, saves: 2.5m, cost: 59),
            Ranked(2, saves: 0, cost: 61.5m),
            Ranked(3, saves: 0.1m, cost: 61.4m),
            Ranked(4, saves: 2, cost: 59.5m),
            Ranked(5, saves: 1.5m, cost: 60),
            Ranked(6, saves: 1.8m, cost: 59.7m, overDetour: true),
            Ranked(7, saves: 0.8m, cost: 60.7m),
            Ranked(8, saves: 0.1m, cost: 61.4m),
        ];
        return new BestNearby(stations[0], 2, 2, 3, stations, 10, true);
    }

    private static RankedStation Ranked(long id, decimal saves, decimal cost = 60, bool overDetour = false, bool uncertain = false) =>
        new(id, null, "Shell", $"{id} rue Principale", 45.5, -73.56, 160, 2, 4, cost, saves, false, overDetour, uncertain);

    private static TripStation OnTheWay(long id, decimal saves, bool reachable = true, bool overDetour = false) =>
        new(id, null, "Shell", $"{id} boul. Saint-Joseph", 45.88, -72.48, 160, 10 * id, 10 * id, 1, 2, 64, saves, false, reachable, overDetour);

    private static TripPlan Plan(long? pickId, long? referenceId, params TripStation[] stations) =>
        new([[-73.5673, 45.5017], [-71.208, 46.8139]], 250, 155, 5, true, null, false, stations, pickId, referenceId, true, null);
}
