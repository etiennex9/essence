using Essence.Contracts;
using Essence.Hours;
using Essence.Ranking;

namespace Essence.Api.Tests;

/// <summary>
/// The stations' hours as the API serves them, and how they enter its caching. Reading <c>station_hours</c> and
/// <c>hours_imports</c> needs PostgreSQL, which these tests don't have: the choice, the snapshot's freshness and the 304
/// rule are checked on values given here.
/// </summary>
public sealed class StationHoursTests
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(21);

    private static SnapshotHoursRow Row(long station, string source, string? rule, double? latitude = 45.5699, double? longitude = -73.7469) =>
        new(new HoursRow(station, source, rule, true, false, null, $"https://example.com/{source}"), latitude, longitude);

    [Fact]
    public void A_station_s_hours_are_its_best_source_s_in_the_contract_s_shape()
    {
        var curatedCheckedAt = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(-5));
        var curated = new SnapshotHoursRow(
            new HoursRow(1, "curated", CostcoRule, true, false, curatedCheckedAt, "https://www.costco.ca/w/-/locations"), 45.5699, -73.7469);

        var hours = StationSnapshot.ChooseHours(
            [curated, Row(1, "costco", "Mo-Fr 06:00-21:30;Sa-Su 06:00-20:30")],
            new Dictionary<string, DateTimeOffset> { ["costco"] = Now.AddDays(-1) },
            Now,
            StaleAfter);

        var chosen = hours[1];
        Assert.Equal(new StationHours(CostcoRule, "costco", Trusted: true, Confirmed: true, null, "https://example.com/costco"), chosen.Dto);
        Assert.Equal(OpeningHours.Parse(CostcoRule), chosen.Hours);
        Assert.Same(QuebecTime.Eastern, chosen.Zone);
    }

    [Fact]
    public void A_stale_Costco_serves_the_curated_rule_instead()
    {
        var hours = StationSnapshot.ChooseHours(
            [Row(1, "costco", "Mo-Fr 06:00-22:00"), Row(1, "curated", CostcoRule)],
            new Dictionary<string, DateTimeOffset> { ["costco"] = Now.AddDays(-22) },
            Now,
            StaleAfter);

        Assert.Equal(("curated", CostcoRule, true), (hours[1].Dto.Source, hours[1].Dto.Rule, hours[1].Dto.Trusted));
    }

    [Fact]
    public void The_islands_hours_are_in_Atlantic_time_and_a_station_without_hours_has_none()
    {
        var hours = StationSnapshot.ChooseHours(
            [Row(1, "osm", "Mo-Su 07:00-21:00", 47.38, -61.86), Row(2, "osm", null), Row(3, "osm", "24/7", null, null)],
            new Dictionary<string, DateTimeOffset> { ["osm"] = Now },
            Now,
            StaleAfter);

        Assert.Same(QuebecTime.Atlantic, hours[1].Zone);
        Assert.False(hours[1].Dto.Trusted);
        Assert.False(hours.ContainsKey(2));
        Assert.Same(QuebecTime.Eastern, hours[3].Zone);
    }

    [Fact]
    public void The_snapshot_is_loaded_again_for_a_new_import_new_hours_or_a_source_gone_stale()
    {
        var feed = Now.AddMinutes(-3);
        var hours = Now.AddDays(-2);
        var snapshot = new Snapshot(feed, [], hours, new Dictionary<long, SnapshotHours>(), Now.AddDays(5), hours);

        Assert.True(snapshot.IsCurrent(feed, hours, Now));
        Assert.False(snapshot.IsCurrent(feed.AddMinutes(5), hours, Now));
        Assert.False(snapshot.IsCurrent(feed, Now, Now));

        // Costco's run, then curated's 60 ms later (both re-matched for a new station): the second is new hours all the same.
        Assert.False(snapshot.IsCurrent(feed, hours.AddMilliseconds(60), Now));
        Assert.True(snapshot.IsCurrent(feed, hours, Now.AddDays(5)));
        Assert.False(snapshot.IsCurrent(feed, hours, Now.AddDays(5).AddSeconds(1)));
        Assert.True((snapshot with { HoursStaleAt = null }).IsCurrent(feed, hours, Now.AddYears(1)));
    }

    [Fact]
    public void A_304_needs_the_feed_and_the_hours_to_be_no_newer_than_what_the_client_has()
    {
        var feed = new DateTimeOffset(2026, 11, 12, 8, 5, 1, TimeSpan.Zero);

        Assert.True(DataVersion.IsNotModified(feed, feed, hoursChangedAt: null));
        Assert.True(DataVersion.IsNotModified(feed, feed, feed.AddDays(-1)));
        Assert.True(DataVersion.IsNotModified(feed, feed, feed));
        Assert.False(DataVersion.IsNotModified(feed, feed, feed.AddSeconds(1)));
        Assert.False(DataVersion.IsNotModified(feed.AddSeconds(-1), feed, null));
        Assert.False(DataVersion.IsNotModified(null, feed, null));
    }

    [Fact]
    public void When_the_hours_changed_is_rounded_up_to_the_second_since_If_Modified_Since_is_to_the_second()
    {
        var at = new DateTimeOffset(2026, 11, 12, 8, 5, 1, TimeSpan.Zero);

        // Hours written 400 ms into the second the feed's version names: a client with that version doesn't have them.
        Assert.False(DataVersion.IsNotModified(at, at, at.AddMilliseconds(400)));
        Assert.True(DataVersion.IsNotModified(at.AddSeconds(1), at, at.AddMilliseconds(400)));
        Assert.True(DataVersion.IsNotModified(at, at, at));
    }

    [Fact]
    public void The_hours_change_when_a_source_goes_stale_with_no_run()
    {
        var run = Now.AddDays(-3);
        var lastSuccesses = new Dictionary<string, DateTimeOffset>
        {
            ["costco"] = Now.AddDays(-22),
            ["osm"] = Now.AddDays(-30),
            ["curated"] = Now.AddDays(-40),
        };

        // Costco went stale a day ago, after the latest run: a client with the feed's version from before has its old hours.
        var changedAt = StationSnapshot.HoursChangedAt(run, lastSuccesses, Now, StaleAfter);
        Assert.Equal(Now.AddDays(-1), changedAt);
        Assert.False(DataVersion.IsNotModified(Now.AddDays(-2), Now.AddDays(-2), changedAt));
        Assert.True(DataVersion.IsNotModified(Now.AddMinutes(-3), Now.AddMinutes(-3), changedAt));

        // A run since counts instead, and nothing gone stale leaves the run's.
        Assert.Equal(Now.AddHours(-1), StationSnapshot.HoursChangedAt(Now.AddHours(-1), lastSuccesses, Now, StaleAfter));
        Assert.Equal(run, StationSnapshot.HoursChangedAt(run, new Dictionary<string, DateTimeOffset> { ["costco"] = Now.AddDays(-2) }, Now, StaleAfter));
        Assert.Null(StationSnapshot.HoursChangedAt(null, new Dictionary<string, DateTimeOffset>(), Now, StaleAfter));
    }
}
