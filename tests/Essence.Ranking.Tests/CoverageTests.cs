using static Essence.Ranking.Tests.TrendData;

namespace Essence.Ranking.Tests;

public class CoverageTests
{
    private static readonly DateTimeOffset Start = Local(11, 10, 0);

    [Fact]
    public void A_moment_is_covered_up_to_10_minutes_after_an_import()
    {
        var coverage = Imports(Start, Start.AddHours(1));
        var last = Start.AddHours(1);

        Assert.True(coverage.IsCovered(last.AddMinutes(10).UtcTicks));
        Assert.False(coverage.IsCovered(last.AddMinutes(11).UtcTicks));
        Assert.False(coverage.IsCovered(Start.AddMinutes(-1).UtcTicks));
    }

    [Fact]
    public void A_gap_isnt_covered_and_the_import_after_it_is_a_resume()
    {
        var gapFrom = Start.AddHours(1);
        var gapTo = Start.AddHours(3);
        var coverage = Imports(Start, Start.AddHours(5), (gapFrom, gapTo));

        Assert.False(coverage.IsCovered(Start.AddHours(2).UtcTicks));
        Assert.True(coverage.IsResume(gapTo.UtcTicks));
        Assert.False(coverage.IsResume(gapTo.AddMinutes(5).UtcTicks));
        Assert.False(coverage.FullyCovered(Start.UtcTicks, Start.AddHours(4).UtcTicks));
        Assert.True(coverage.FullyCovered(gapTo.UtcTicks, Start.AddHours(5).UtcTicks));
        Assert.True(coverage.AnyCovered(Start.AddHours(2).UtcTicks, gapTo.UtcTicks));
        Assert.False(coverage.AnyCovered(Start.AddHours(1.5).UtcTicks, Start.AddHours(2.5).UtcTicks));
    }

    [Fact]
    public void The_latest_import_at_or_before_a_moment()
    {
        var coverage = Imports(Start, Start.AddHours(1));

        Assert.Equal(Start.AddMinutes(10).UtcTicks, coverage.LatestAtOrBefore(Start.AddMinutes(14).UtcTicks));
        Assert.Equal(Start.AddMinutes(15).UtcTicks, coverage.LatestAtOrBefore(Start.AddMinutes(15).UtcTicks));
        Assert.Null(coverage.LatestAtOrBefore(Start.AddSeconds(-1).UtcTicks));
    }

    [Fact]
    public void With_drops_imports_before_the_window()
    {
        var coverage = Imports(Start, Start.AddHours(1)).With([Start.AddHours(2).UtcTicks], Start.AddMinutes(30).UtcTicks);

        Assert.Equal(Start.AddMinutes(30).UtcTicks, coverage.First);
        Assert.Equal(Start.AddHours(2).UtcTicks, coverage.Last);
    }
}
