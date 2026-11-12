using Essence.Hours;

namespace Essence.Ranking.Tests;

public class HoursPrecedenceTests
{
    private const long Laval = 42;
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    /// <summary>Every source succeeded yesterday.</summary>
    private static readonly Dictionary<string, DateTimeOffset> Fresh = new()
    {
        [HoursPrecedence.Costco] = Now.AddDays(-1),
        [HoursPrecedence.CircleK] = Now.AddDays(-1),
        [HoursPrecedence.Ultramar] = Now.AddDays(-1),
        [HoursPrecedence.Shell] = Now.AddDays(-1),
        [HoursPrecedence.Osm] = Now.AddDays(-1),
        ["acme"] = Now.AddDays(-1),
    };

    private static HoursRow Row(string source, string? rule = "Mo-Su 06:00-22:00", bool confirmed = true, long station = Laval) =>
        new(station, source, rule, confirmed, TemporarilyClosed: false, CheckedAt: null, Link: null);

    [Fact]
    public void Costco_s_own_hours_beat_the_curated_rule_and_are_trusted()
    {
        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Curated, "Mo-Su 07:00-21:00"), Row(HoursPrecedence.Costco, CostcoRule)], Fresh, Now);

        var hours = chosen[Laval];
        Assert.Equal(HoursPrecedence.Costco, hours.Row.Source);
        Assert.Equal(OpeningHours.Parse(CostcoRule), hours.Hours);
        Assert.True(hours.Trusted);
    }

    [Theory]
    [InlineData(HoursPrecedence.Costco)]
    [InlineData(HoursPrecedence.Curated)]
    [InlineData(HoursPrecedence.CircleK)]
    [InlineData(HoursPrecedence.Ultramar)]
    [InlineData(HoursPrecedence.Shell)]
    [InlineData("osm confirmed")]
    [InlineData("osm")]
    [InlineData("acme")]
    public void Each_source_beats_those_after_it(string winner)
    {
        HoursRow[] ranked =
        [
            Row(HoursPrecedence.Costco), Row(HoursPrecedence.Curated), Row(HoursPrecedence.CircleK), Row(HoursPrecedence.Ultramar),
            Row(HoursPrecedence.Shell), Row(HoursPrecedence.Osm, confirmed: true), Row(HoursPrecedence.Osm, confirmed: false), Row("acme"),
        ];
        var index = Array.FindIndex(ranked, r => winner == "osm confirmed" ? r is { Source: HoursPrecedence.Osm, Confirmed: true }
            : winner == HoursPrecedence.Osm ? r is { Source: HoursPrecedence.Osm, Confirmed: false }
            : r.Source == winner);

        // From the winner on, in reverse: the order they come in doesn't matter.
        var chosen = HoursPrecedence.Choose(ranked[index..].Reverse(), Fresh, Now)[Laval];

        Assert.Same(ranked[index], chosen.Row);
    }

    [Theory]
    [InlineData(HoursPrecedence.Costco, true)]
    [InlineData(HoursPrecedence.Curated, true)]
    [InlineData(HoursPrecedence.CircleK, true)]
    [InlineData(HoursPrecedence.Ultramar, true)]
    [InlineData(HoursPrecedence.Shell, true)]
    [InlineData(HoursPrecedence.Osm, false)]
    [InlineData("acme", false)]
    public void Brands_and_Essence_are_trusted_and_OpenStreetMap_and_unknown_sources_only_shown(string source, bool trusted) =>
        Assert.Equal(trusted, HoursPrecedence.Choose([Row(source)], Fresh, Now)[Laval].Trusted);

    [Fact]
    public void A_stale_Costco_falls_behind_the_curated_rule()
    {
        var lastSuccesses = new Dictionary<string, DateTimeOffset>(Fresh) { [HoursPrecedence.Costco] = Now.AddDays(-22) };

        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Costco, CostcoRule), Row(HoursPrecedence.Curated, "Mo-Su 07:00-21:00")], lastSuccesses, Now);

        Assert.Equal(HoursPrecedence.Curated, chosen[Laval].Row.Source);
        Assert.True(chosen[Laval].Trusted);
    }

    [Fact]
    public void A_stale_source_comes_after_every_fresh_one_and_is_shown_untrusted_when_it_s_all_there_is()
    {
        var lastSuccesses = new Dictionary<string, DateTimeOffset>(Fresh) { [HoursPrecedence.Costco] = Now.AddDays(-22) };

        var behindOsm = HoursPrecedence.Choose([Row(HoursPrecedence.Costco), Row(HoursPrecedence.Osm, confirmed: false)], lastSuccesses, Now);
        var alone = HoursPrecedence.Choose([Row(HoursPrecedence.Costco)], lastSuccesses, Now);

        Assert.Equal(HoursPrecedence.Osm, behindOsm[Laval].Row.Source);
        Assert.Equal((HoursPrecedence.Costco, false), (alone[Laval].Row.Source, alone[Laval].Trusted));
    }

    [Fact]
    public void A_source_that_never_succeeded_is_stale()
    {
        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Shell)], new Dictionary<string, DateTimeOffset>(), Now);

        Assert.False(chosen[Laval].Trusted);
        Assert.True(HoursPrecedence.IsStale(HoursPrecedence.Shell, new Dictionary<string, DateTimeOffset>(), Now, TimeSpan.FromDays(21)));
    }

    [Fact]
    public void The_curated_rules_never_go_stale()
    {
        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Curated)], new Dictionary<string, DateTimeOffset>(), Now);

        Assert.True(chosen[Laval].Trusted);
    }

    [Fact]
    public void Stale_is_past_21_days_without_a_success()
    {
        var lastSuccesses = new Dictionary<string, DateTimeOffset> { [HoursPrecedence.Costco] = Now.AddDays(-21) };

        Assert.False(HoursPrecedence.IsStale(HoursPrecedence.Costco, lastSuccesses, Now, HoursPrecedence.DefaultStaleAfter));
        Assert.True(HoursPrecedence.IsStale(HoursPrecedence.Costco, lastSuccesses, Now.AddSeconds(1), HoursPrecedence.DefaultStaleAfter));
        Assert.True(HoursPrecedence.IsStale(HoursPrecedence.Costco, lastSuccesses, Now.AddDays(1), TimeSpan.FromDays(21.5)));
    }

    [Fact]
    public void Rows_without_hours_or_whose_hours_can_t_be_read_are_passed_over()
    {
        var chosen = HoursPrecedence.Choose(
        [
            Row(HoursPrecedence.Costco, rule: null),
            Row(HoursPrecedence.Curated, rule: "Mo-Fr 08:00+"),
            Row(HoursPrecedence.Osm),
            Row(HoursPrecedence.Shell, rule: "sunrise-sunset", station: 7),
        ], Fresh, Now);

        Assert.Equal(HoursPrecedence.Osm, chosen[Laval].Row.Source);
        Assert.False(chosen.ContainsKey(7));
    }

    [Fact]
    public void A_rule_written_loosely_still_counts()
    {
        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Costco, rule: "Mo-Fr 6:00-21:30, Sa-Su 06:00-20:30")], Fresh, Now);

        Assert.Equal(OpeningHours.Parse(CostcoRule), chosen[Laval].Hours);
    }

    [Fact]
    public void Each_station_gets_its_own()
    {
        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Costco, station: 1), Row(HoursPrecedence.Osm, station: 2)], Fresh, Now);

        Assert.Equal((HoursPrecedence.Costco, HoursPrecedence.Osm), (chosen[1].Row.Source, chosen[2].Row.Source));
    }

    [Fact]
    public void The_hours_change_when_the_first_fresh_source_goes_stale()
    {
        var lastSuccesses = new Dictionary<string, DateTimeOffset>
        {
            [HoursPrecedence.Costco] = Now.AddDays(-3),
            [HoursPrecedence.Osm] = Now.AddDays(-1),
            [HoursPrecedence.Shell] = Now.AddDays(-30),
            [HoursPrecedence.Curated] = Now.AddDays(-25),
        };

        Assert.Equal(Now.AddDays(18), HoursPrecedence.StaleAt(lastSuccesses, Now));
        Assert.Null(HoursPrecedence.StaleAt(new Dictionary<string, DateTimeOffset> { [HoursPrecedence.Curated] = Now }, Now));
    }

    [Fact]
    public void The_hours_changed_when_the_latest_source_went_stale()
    {
        var lastSuccesses = new Dictionary<string, DateTimeOffset>
        {
            [HoursPrecedence.Costco] = Now.AddDays(-3),
            [HoursPrecedence.Osm] = Now.AddDays(-23),
            [HoursPrecedence.Shell] = Now.AddDays(-30),
            [HoursPrecedence.Curated] = Now.AddDays(-25),
        };

        Assert.Equal(Now.AddDays(-2), HoursPrecedence.StaleSince(lastSuccesses, Now));
        Assert.Equal(Now.AddDays(-9), HoursPrecedence.StaleSince(lastSuccesses, Now.AddDays(-2)));
        Assert.Equal(Now.AddDays(-2), HoursPrecedence.StaleSince(lastSuccesses, Now.AddDays(-2).AddTicks(1)));
        Assert.Null(HoursPrecedence.StaleSince(new Dictionary<string, DateTimeOffset> { [HoursPrecedence.Curated] = Now.AddDays(-90) }, Now));
    }
}
