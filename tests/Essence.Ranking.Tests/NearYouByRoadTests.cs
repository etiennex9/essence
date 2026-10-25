namespace Essence.Ranking.Tests;

public class NearYouByRoadTests
{
    [Fact]
    public void The_ten_nearest_are_measured_then_every_cheaper_one_nearest_first()
    {
        // The nearest sells at 165; every third station farther away is cheaper.
        var stations = Enumerable.Range(0, 300).Select(i => (Id: i, Km: i * 0.1, Price: i % 3 == 0 && i > 0 ? 160m : 165m)).ToList();

        var measured = NearYou.SelectByRoad(stations, s => s.Km, s => s.Price);

        Assert.Equal(Enumerable.Range(0, 10), measured.Take(10).Select(s => s.Id));
        Assert.All(measured.Skip(10), s => Assert.Equal(160m, s.Price));
        Assert.Equal(measured.OrderBy(s => s.Km), measured);
        Assert.Equal(10 + 96, measured.Count);
    }

    [Fact]
    public void At_most_200_are_measured()
    {
        var stations = Enumerable.Range(0, 500).Select(i => (Km: i * 0.1, Price: i == 0 ? 170m : 160m)).ToList();

        Assert.Equal(NearYou.MaxByRoad, NearYou.SelectByRoad(stations, s => s.Km, s => s.Price).Count);
    }

    [Fact]
    public void Nothing_to_measure_measures_nothing() =>
        Assert.Empty(NearYou.SelectByRoad(Array.Empty<(double Km, decimal Price)>(), s => s.Km, s => s.Price));

    [Fact]
    public void Candidates_by_road_count_there_and_back()
    {
        (long, decimal)[] stations = [(1, 166.9m), (2, 155.9m), (3, 160m), (4, 158m)];
        OsrmWaypoint[] onRoads = [.. Enumerable.Repeat(new OsrmWaypoint(new(45.5, -73.5), 0), 4)];
        var outbound = new OsrmTable([[800, 9_000, null, 2_000]], [[96, 960, null, 200]], [onRoads[0]], onRoads);
        var back = new OsrmTable([[1_100], [9_400], [3_000], [null]], [[120], [1_000], [300], [null]], onRoads, [onRoads[0]]);

        var candidates = NearYou.ByRoad(stations, outbound, back);

        // 3 has no road there, and 4 none back.
        Assert.Equal(
            [new Candidate(1, 166.9m, 0.8, 1.9, 216 / 60.0, 96 / 60.0), new Candidate(2, 155.9m, 9, 18.4, 1_960 / 60.0, 16)],
            candidates);
    }

    [Fact]
    public void Road_candidates_rank_without_the_cautious_check()
    {
        // By road, the Costco 9 km away saves its $2.24 even though twice the straight line would undo it.
        Candidate[] candidates = [new(1, 166.9m, 0.8, 1.6, 2.4, 1.2), new(2, 155.9m, 9, 18, 9, 4.5)];

        var ranking = NearYou.Rank(candidates, new Car(40, 8.5m), 10, byRoad: true)!;

        Assert.Equal(2, ranking.Pick.Id);
        Assert.False(ranking.Pick.SavingUncertain);
    }
}
