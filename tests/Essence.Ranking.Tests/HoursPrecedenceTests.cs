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
    [InlineData(HoursPrecedence.Ultramar)]
    [InlineData(HoursPrecedence.Shell)]
    [InlineData(HoursPrecedence.CircleK)]
    [InlineData("osm confirmed")]
    [InlineData("osm")]
    [InlineData("acme")]
    public void Each_source_beats_those_after_it(string winner)
    {
        HoursRow[] ranked =
        [
            Row(HoursPrecedence.Costco), Row(HoursPrecedence.Curated), Row(HoursPrecedence.Ultramar), Row(HoursPrecedence.Shell),
            Row(HoursPrecedence.CircleK), Row(HoursPrecedence.Osm, confirmed: true), Row(HoursPrecedence.Osm, confirmed: false), Row("acme"),
        ];
        var index = Array.FindIndex(ranked, r => winner == "osm confirmed" ? r is { Source: HoursPrecedence.Osm, Confirmed: true }
            : winner == HoursPrecedence.Osm ? r is { Source: HoursPrecedence.Osm, Confirmed: false }
            : r.Source == winner);

        // From the winner on, in reverse: the order they come in doesn't matter.
        var chosen = HoursPrecedence.Choose(ranked[index..].Reverse(), Fresh, Now)[Laval];

        Assert.Same(ranked[index], chosen.Row);
    }

    [Fact]
    public void A_forecourt_s_hours_beat_the_Couche_Tard_store_s_at_the_same_station()
    {
        // A Couche-Tard that sells Shell's gas: the store opens at 5, the pumps at 6.
        var chosen = HoursPrecedence.Choose(
            [Row(HoursPrecedence.CircleK, "Mo-Su 05:00-23:00"), Row(HoursPrecedence.Shell, "Mo-Su 06:00-22:00")], Fresh, Now)[Laval];

        Assert.Equal((HoursPrecedence.Shell, true), (chosen.Row.Source, chosen.Trusted));
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
    public void An_OpenStreetMap_element_matched_by_location_only_gives_no_hours_another_source_s_showing_or_none()
    {
        // The ingest stores such a match unconfirmed and without the element's hours.
        var byLocation = Row(HoursPrecedence.Osm, rule: null, confirmed: false);

        var alone = HoursPrecedence.Choose([byLocation], Fresh, Now);
        var withShell = HoursPrecedence.Choose([byLocation, Row(HoursPrecedence.Shell, rule: "24/7")], Fresh, Now);

        Assert.Empty(alone);
        Assert.Equal((HoursPrecedence.Shell, true), (withShell[Laval].Row.Source, withShell[Laval].Trusted));
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

    private static readonly Dictionary<string, HoursSourceProfile> Plugins = new()
    {
        ["crevier"] = new("Crevier", HoursSourceKind.Brand, HoursSourceProfile.DefaultBrandRank),
        ["villedata"] = new("Données de la ville", HoursSourceKind.OpenData, HoursSourceProfile.DefaultOpenDataRank),
    };

    private static Dictionary<string, DateTimeOffset> WithPlugins(int daysAgo = 1) =>
        new(Fresh) { ["crevier"] = Now.AddDays(-daysAgo), ["villedata"] = Now.AddDays(-daysAgo) };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_brand_s_plugin_comes_after_the_brands_in_code_and_before_OpenStreetMap_and_open_data_after_it(int index)
    {
        HoursRow[] ranked =
        [
            Row(HoursPrecedence.CircleK), Row("crevier"), Row(HoursPrecedence.Osm, confirmed: true), Row(HoursPrecedence.Osm, confirmed: false),
            Row("villedata", confirmed: true), Row("villedata", confirmed: false), Row("acme"),
        ];

        var chosen = HoursPrecedence.Choose(ranked[index..].Reverse(), WithPlugins(), Now, profiles: Plugins)[Laval];

        Assert.Same(ranked[index], chosen.Row);
    }

    [Theory]
    [InlineData("crevier", true)]
    [InlineData("villedata", false)]
    public void A_brand_s_plugin_is_trusted_and_open_data_only_shown(string source, bool trusted) =>
        Assert.Equal(trusted, HoursPrecedence.Choose([Row(source)], WithPlugins(), Now, profiles: Plugins)[Laval].Trusted);

    [Fact]
    public void A_stale_plugin_falls_behind_fresh_OpenStreetMap_and_isn_t_trusted()
    {
        var chosen = HoursPrecedence.Choose(
            [Row("crevier"), Row(HoursPrecedence.Osm, confirmed: false)], WithPlugins(daysAgo: 30), Now, profiles: Plugins)[Laval];

        Assert.Equal((HoursPrecedence.Osm, false), (chosen.Row.Source, chosen.Trusted));
    }

    [Fact]
    public void A_plugin_s_spec_may_rank_it_anywhere_and_one_taking_a_built_in_s_name_and_rank_ranks_as_it_did()
    {
        var first = new Dictionary<string, HoursSourceProfile> { ["crevier"] = new("Crevier", HoursSourceKind.Brand, 5) };
        Assert.Equal("crevier", HoursPrecedence.Choose([Row(HoursPrecedence.Costco), Row("crevier")], WithPlugins(), Now, profiles: first)[Laval].Row.Source);

        // Ultramar's locator as a plugin, stored with Ultramar's name and rank: still after the curated rules, before Shell.
        var converted = new Dictionary<string, HoursSourceProfile> { [HoursPrecedence.Ultramar] = new("Ultramar", HoursSourceKind.Brand, 30) };
        Assert.Equal(
            HoursPrecedence.Curated,
            HoursPrecedence.Choose([Row(HoursPrecedence.Ultramar), Row(HoursPrecedence.Curated)], Fresh, Now, profiles: converted)[Laval].Row.Source);
        Assert.Equal(
            HoursPrecedence.Ultramar,
            HoursPrecedence.Choose([Row(HoursPrecedence.Shell), Row(HoursPrecedence.Ultramar)], Fresh, Now, profiles: converted)[Laval].Row.Source);
    }

    [Fact]
    public void The_sources_in_code_keep_their_order_as_profiles_with_the_plugins_defaults_between()
    {
        var ranks = new[] { HoursPrecedence.Costco, HoursPrecedence.Curated, HoursPrecedence.Ultramar, HoursPrecedence.Shell, HoursPrecedence.CircleK, HoursPrecedence.Osm }
            .Select(s => HoursPrecedence.BuiltIn[s].Rank)
            .ToList();

        Assert.Equal(ranks.Order(), ranks);
        Assert.InRange(HoursSourceProfile.DefaultBrandRank, HoursPrecedence.BuiltIn[HoursPrecedence.CircleK].Rank + 1, HoursPrecedence.BuiltIn[HoursPrecedence.Osm].Rank - 1);
        Assert.True(HoursSourceProfile.DefaultOpenDataRank > HoursPrecedence.BuiltIn[HoursPrecedence.Osm].Rank + 1);
    }

    [Fact]
    public void The_ranks_stored_are_these_exactly()
    {
        // Stored in hours_sources and documented (README, Hours/Plugins/README.md): a plugin that takes over a source's name
        // takes its rank, so a change here is a change of the served order, not a detail.
        Assert.Equal(
            [
                ("costco", "Costco", HoursSourceKind.Brand, 10), ("curated", "Essence", HoursSourceKind.Curated, 20),
                ("ultramar", "Ultramar", HoursSourceKind.Brand, 30), ("shell", "Shell", HoursSourceKind.Brand, 31),
                ("circlek", "Couche-Tard", HoursSourceKind.Brand, 40), ("osm", "OpenStreetMap", HoursSourceKind.Osm, 60),
            ],
            HoursPrecedence.BuiltIn.OrderBy(p => p.Value.Rank).Select(p => (p.Key, p.Value.Label, p.Value.Kind, p.Value.Rank)));
        Assert.Equal((50, 80, 90), (HoursSourceProfile.DefaultBrandRank, HoursSourceProfile.DefaultOpenDataRank, HoursPrecedence.UnknownRank));
    }

    [Theory]
    [InlineData(60, true, "acme")]
    [InlineData(61, true, HoursPrecedence.Osm)]
    [InlineData(61, false, "acme")]
    [InlineData(62, false, HoursPrecedence.Osm)]
    public void OpenStreetMap_ranks_60_confirmed_and_61_not(int rank, bool confirmed, string expected)
    {
        // "acme" sorts before "osm": at the same rank it's chosen, so the rank at which it stops being chosen is OSM's.
        var profiles = new Dictionary<string, HoursSourceProfile> { ["acme"] = new("Acme", HoursSourceKind.Brand, rank) };

        var chosen = HoursPrecedence.Choose([Row(HoursPrecedence.Osm, confirmed: confirmed), Row("acme")], Fresh, Now, profiles: profiles);

        Assert.Equal(expected, chosen[Laval].Row.Source);
    }

    [Theory]
    [InlineData(HoursSourceKind.Brand, "brand")]
    [InlineData(HoursSourceKind.Curated, "curated")]
    [InlineData(HoursSourceKind.Osm, "osm")]
    [InlineData(HoursSourceKind.OpenData, "open-data")]
    public void Kinds_are_stored_by_name(HoursSourceKind kind, string name)
    {
        Assert.Equal(name, HoursSourceKinds.Name(kind));
        Assert.True(HoursSourceKinds.TryParse(name.ToUpperInvariant(), out var parsed));
        Assert.Equal(kind, parsed);
    }
}
