using static Essence.Ranking.Tests.TrendData;

namespace Essence.Ranking.Tests;

public class CheapStationsTests
{
    private static readonly DateTimeOffset Start = Local(11, 2, 0);

    /// <summary>Ten days of imports every 5 minutes: 481 half-hours counted.</summary>
    private static readonly DateTimeOffset End = Start.AddDays(10);

    /// <summary>A station about 300 m east of the last, at one price all along.</summary>
    private static CheapCandidate At(int index, decimal cents, bool membersOnly = false, double kmApart = 0.3) =>
        Candidate(index, membersOnly, kmApart, (Start, cents));

    private static CheapCandidate Candidate(int index, bool membersOnly, double kmApart, params (DateTimeOffset At, decimal? Cents)[] changes) =>
        new(index + 1, 45.5, -73.6 + index * kmApart / 78.0, membersOnly, Prices(changes));

    private static UsualCheapness Find(params CheapCandidate[] stations) => Find(Imports(Start, End), stations);

    private static UsualCheapness Find(Coverage coverage, params CheapCandidate[] stations) =>
        CheapStations.Find(stations, coverage, End.AddMinutes(3))!;

    [Fact]
    public void The_cheapest_of_its_neighbours_all_along_is_usually_cheap()
    {
        var found = Find(At(0, 150m), At(1, 160m), At(2, 160m), At(3, 161m), At(4, 162m), At(5, 165m));

        Assert.Equal([1L], found.Stations.Ids);
        Assert.Equal(6, found.Rated);
        Assert.Equal(10, found.Days);
        Assert.Equal(End, found.AsOf);
    }

    [Fact]
    public void Fewer_than_four_neighbours_with_a_price_rate_no_one()
    {
        var found = Find(At(0, 150m), At(1, 160m), At(2, 160m), At(3, 161m));

        Assert.Equal(0, found.Rated);
        Assert.Empty(found.Stations.Ids);
    }

    [Fact]
    public void Stations_at_the_same_price_count_half()
    {
        // Two at 150 among four at 160: each has one of five at its price, a rank of 10 %.
        Assert.Equal([1L, 2L], Find(At(0, 150m), At(1, 150m), At(2, 160m), At(3, 160m), At(4, 160m), At(5, 160m)).Stations.Ids);

        // Four at 150 and two at 160: three of five at its price, 30 %.
        Assert.Empty(Find(At(0, 150m), At(1, 150m), At(2, 150m), At(3, 150m), At(4, 160m), At(5, 160m)).Stations.Ids);
    }

    [Fact]
    public void Costco_is_rated_but_no_one_is_compared_with_it()
    {
        var found = Find(At(0, 140m, membersOnly: true), At(1, 150m), At(2, 160m), At(3, 160m), At(4, 161m), At(5, 162m));

        Assert.Equal([1L, 2L], found.Stations.Ids);
    }

    [Fact]
    public void Stations_more_than_10_km_apart_are_not_neighbours()
    {
        var found = Find(At(0, 150m, kmApart: 11), At(1, 160m, kmApart: 11), At(2, 160m, kmApart: 11), At(3, 161m, kmApart: 11), At(4, 162m, kmApart: 11));

        Assert.Equal(0, found.Rated);
    }

    [Fact]
    public void Cheap_on_half_the_moments_is_usually_cheap_but_not_on_a_third()
    {
        var others = new[] { At(1, 160m), At(2, 160m), At(3, 161m), At(4, 162m), At(5, 165m) };
        var half = Candidate(0, false, 0.3, (Start, 150m), (Start.AddDays(5), 170m));
        var third = Candidate(0, false, 0.3, (Start, 150m), (Start.AddDays(3.3), 170m));

        Assert.True(Find([half, .. others]).Stations.Contains(1));
        Assert.False(Find([third, .. others]).Stations.Contains(1));
    }

    [Fact]
    public void Moments_the_data_does_not_cover_do_not_count()
    {
        // Ten days with four of them missing: six days of half-hours, fewer than a week's.
        var coverage = Imports(Start, End, (Start.AddDays(3), Start.AddDays(7)));

        var found = Find(coverage, At(0, 150m), At(1, 160m), At(2, 160m), At(3, 161m), At(4, 162m), At(5, 165m));

        Assert.Equal(0, found.Rated);
    }

    [Fact]
    public void A_moment_without_its_own_price_does_not_count()
    {
        // Unavailable for four days of ten: six days counted.
        var gone = Candidate(0, false, 0.3, (Start, 150m), (Start.AddDays(3), null), (Start.AddDays(7), 150m));

        var found = Find(gone, At(1, 160m), At(2, 160m), At(3, 161m), At(4, 162m), At(5, 165m));

        Assert.Equal(5, found.Rated);
        Assert.False(found.Stations.Contains(1));
    }

    [Fact]
    public void Only_the_last_30_days_are_compared()
    {
        // Cheapest for the first 20 of 40 days, dearest since: a third of the last 30.
        var start = End.AddDays(-40);
        var station = Candidate(0, false, 0.3, (start, 150m), (start.AddDays(20), 170m));
        var others = new[] { 160m, 160m, 161m, 162m, 165m }.Select((cents, i) => Candidate(i + 1, false, 0.3, (start, cents))).ToArray();

        var found = Find(Imports(start, End), [station, .. others]);

        Assert.Equal(30, found.Days);
        Assert.False(found.Stations.Contains(1));
    }

    [Fact]
    public void Nothing_before_the_first_import()
    {
        Assert.Null(CheapStations.Find([At(0, 150m)], Coverage.None, End));
    }
}
