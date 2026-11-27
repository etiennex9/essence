using static Essence.Ranking.Tests.TrendData;

namespace Essence.Ranking.Tests;

public class TrendsTests
{
    private static readonly DateTimeOffset Noon = Local(11, 20, 12);

    [Fact]
    public void The_median_of_an_even_count_is_the_lower_middle_price()
    {
        var coverage = Imports(Noon.AddDays(-8), Noon);
        AreaStation[] area =
        [
            Station(1, (Noon.AddDays(-10), 150.9m)),
            Station(2, (Noon.AddDays(-10), 151.9m)),
            Station(3, (Noon.AddDays(-10), 152.9m)),
            Station(4, (Noon.AddDays(-10), 153.9m)),
        ];

        var figures = Trends.Compute(area, coverage, Noon, Zone)!;

        Assert.Equal(new AreaSample(150.9m, 151.9m, 4), figures.Now);
        Assert.Equal(Noon, figures.AsOf);
    }

    [Fact]
    public void Yesterday_is_the_same_time_on_the_clock_across_a_change_of_time()
    {
        // 2026-11-01 is the first Sunday of November: noon on the 1st is 25 hours after noon on October 31.
        var noon = Local(11, 1, 12);

        var yesterday = Trends.SameTimeBefore(noon, Zone, 1);

        Assert.Equal(noon.AddHours(-25), yesterday);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(-4)), yesterday);
    }

    [Fact]
    public void Yesterday_and_last_week_compare_the_stations_priced_both_times()
    {
        var coverage = Imports(Noon.AddDays(-8), Noon);
        AreaStation[] area =
        [
            Station(1, (Noon.AddDays(-10), 160.9m), (Noon.AddHours(-1), 150.9m)),
            Station(2, (Noon.AddDays(-10), 162.9m), (Noon.AddHours(-1), 152.9m)),
            Station(3, (Noon.AddDays(-10), 164.9m), (Noon.AddHours(-1), 154.9m)),
            Station(4, (Noon.AddHours(-2), 140.9m)),
        ];

        var figures = Trends.Compute(area, coverage, Noon, Zone)!;

        Assert.Equal(new AreaComparison(Noon.AddDays(-1), 162.9m, 152.9m, 3), figures.Yesterday);
        Assert.Equal(new AreaComparison(Noon.AddDays(-7), 162.9m, 152.9m, 3), figures.LastWeek);
        Assert.Equal(140.9m, figures.Now!.Value.LowestCents);
    }

    [Fact]
    public void A_gap_yesterday_gives_no_comparison()
    {
        var coverage = Imports(Noon.AddDays(-8), Noon, (Noon.AddDays(-1).AddHours(-2), Noon.AddDays(-1).AddHours(2)));
        AreaStation[] area = [.. Enumerable.Range(1, 3).Select(i => Station(i, (Noon.AddDays(-10), 160.9m)))];

        var figures = Trends.Compute(area, coverage, Noon, Zone)!;

        Assert.Null(figures.Yesterday);
        Assert.NotNull(figures.LastWeek);
    }

    [Fact]
    public void The_24_hour_low_leaves_out_a_correction_and_prefers_the_latest()
    {
        var coverage = Imports(Noon.AddDays(-2), Noon);
        AreaStation[] area =
        [
            // 140.9 for 10 minutes: a correction, not a price.
            Station(1, (Noon.AddDays(-3), 160.9m), (Noon.AddHours(-5), 140.9m), (Noon.AddHours(-5).AddMinutes(10), 160.9m)),
            // 150.9 for an hour, then back up.
            Station(2, (Noon.AddDays(-3), 160.9m), (Noon.AddHours(-6), 150.9m), (Noon.AddHours(-5), 160.9m)),
            // 150.9 since an hour ago: still the price, so it wins the tie.
            Station(3, (Noon.AddDays(-3), 160.9m), (Noon.AddHours(-1), 150.9m)),
        ];

        var low = Trends.Compute(area, coverage, Noon, Zone)!.Low!;

        Assert.Equal(new AreaLowest(3, 150.9m, Noon.AddHours(-1), null, StillNow: true), low);
    }

    [Fact]
    public void The_24_hour_low_starts_24_hours_ago_at_the_earliest_and_has_its_end()
    {
        var coverage = Imports(Noon.AddDays(-2), Noon);
        AreaStation[] area = [Station(1, (Noon.AddDays(-3), 150.9m), (Noon.AddHours(-2), 160.9m))];

        var low = Trends.Compute(area, coverage, Noon, Zone)!.Low!;

        Assert.Equal(new AreaLowest(1, 150.9m, Noon.AddDays(-1), Noon.AddHours(-2), StillNow: false), low);
    }

    [Fact]
    public void A_low_that_ended_during_a_gap_has_no_end()
    {
        var coverage = Imports(Noon.AddDays(-2), Noon, (Noon.AddHours(-6), Noon.AddHours(-4)));
        AreaStation[] area = [Station(1, (Noon.AddDays(-3), 150.9m), (Noon.AddHours(-4), 160.9m))];

        var low = Trends.Compute(area, coverage, Noon, Zone)!.Low!;

        Assert.Equal(new AreaLowest(1, 150.9m, Noon.AddDays(-1), null, StillNow: false), low);
    }

    [Fact]
    public void The_last_24_hours_are_sampled_every_quarter_hour_with_gaps()
    {
        var asOf = Noon.AddMinutes(5);
        var coverage = Imports(Noon.AddDays(-2), asOf, (Noon.AddHours(-6), Noon.AddHours(-4)));
        AreaStation[] area = [Station(1, (Noon.AddDays(-3), 150.9m)), Station(2, (Noon.AddDays(-3), 152.9m))];

        var figures = Trends.Compute(area, coverage, asOf, Zone)!;

        Assert.Equal(Noon.AddDays(-1).AddMinutes(15), figures.SeriesFrom);
        Assert.Equal(96, figures.Series.Count);
        Assert.Contains(null, figures.Series);
        Assert.Equal(new AreaSample(150.9m, 150.9m, 2), figures.Series[^1]);
    }

    [Fact]
    public void A_sample_on_the_slot_reads_the_import_that_landed_just_after()
    {
        // Imports land a second or two after their slot.
        var late = TimeSpan.FromSeconds(1.7);
        var coverage = Coverage.Of(Slots(Noon.AddDays(-2), Noon).Select(at => at + late));
        var prices = Prices((Noon.AddDays(-3), 160.9m), (Noon.AddHours(-3) + late, 170.9m));

        Assert.Equal(1709, Trends.ReadAt(prices, coverage, Noon.AddHours(-3).UtcTicks, (Noon + late).UtcTicks));
    }

    [Fact]
    public void Now_is_the_latest_import_at_or_before_the_clock()
    {
        var coverage = Imports(Noon.AddDays(-2), Noon.AddHours(2));
        AreaStation[] area = [Station(1, (Noon.AddDays(-3), 150.9m), (Noon.AddHours(1), 170.9m))];

        var figures = Trends.Compute(area, coverage, Noon.AddMinutes(3), Zone)!;

        Assert.Equal(Noon, figures.AsOf);
        Assert.Equal(150.9m, figures.Now!.Value.MedianCents);
    }

    [Fact]
    public void No_import_yet_gives_nothing()
    {
        Assert.Null(Trends.Compute([Station(1, (Noon, 150.9m))], Coverage.None, Noon, Zone));
    }
}
