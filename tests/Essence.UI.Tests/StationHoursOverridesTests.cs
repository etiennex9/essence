using Essence.Contracts;
using Essence.Hours;
using Essence.UI.App;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

/// <summary>The hours the user corrected, kept on the device: per server, read leniently, and always winning over the server's.</summary>
public class StationHoursOverridesTests
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";

    private readonly MemoryPreferences _preferences = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero));

    private static readonly StationHours Server = new("24/7", "osm", Trusted: false, Confirmed: true, CheckedAt: null, "https://www.openstreetmap.org/node/1");

    [Fact]
    public async Task A_correction_is_saved_canonical_and_read_back_with_when_and_for_which_station()
    {
        var overrides = new StationHoursOverrides(_preferences, _time);
        await overrides.SaveAsync(12, OpeningHours.Parse("Mo-Fr 06:00-21:30;Sa-Su 06:00-20:30", lenient: true), "Costco", "1 rue Principale");

        var reloaded = new StationHoursOverrides(_preferences, _time);
        await reloaded.LoadAsync();

        Assert.Equal(new HoursCorrection(CostcoRule, _time.GetUtcNow(), "Costco", "1 rue Principale"), reloaded.Find(12));
        Assert.True(reloaded.Has(12));
        Assert.False(reloaded.Has(13));
    }

    [Fact]
    public async Task The_device_s_hours_win_with_their_own_source_trusted_and_open_whatever_the_server_said()
    {
        var overrides = new StationHoursOverrides(_preferences, _time);
        await overrides.SaveAsync(12, OpeningHours.Parse(CostcoRule), null, null);

        Assert.Equal(
            new StationHours(CostcoRule, "device", Trusted: true, Confirmed: true, _time.GetUtcNow(), Link: null),
            overrides.Apply(12, Server with { TemporarilyClosed = true }));
        Assert.Equal(new StationHours(CostcoRule, "device", true, true, _time.GetUtcNow(), null), overrides.Apply(12, null));
        Assert.Same(Server, overrides.Apply(13, Server));
        Assert.Null(overrides.Apply(13, null));
    }

    [Fact]
    public async Task Resetting_forgets_the_correction_and_the_server_s_hours_show_again()
    {
        var overrides = new StationHoursOverrides(_preferences, _time);
        await overrides.SaveAsync(12, OpeningHours.Parse(CostcoRule), null, null);
        await overrides.SaveAsync(13, OpeningHours.AlwaysOpen, null, null);
        var changes = 0;
        overrides.Changed += () => changes++;

        await overrides.ResetAsync(12);
        await overrides.ResetAsync(12);

        Assert.Equal(1, changes);
        Assert.Same(Server, overrides.Apply(12, Server));
        var reloaded = new StationHoursOverrides(_preferences, _time);
        await reloaded.LoadAsync();
        Assert.Equal([13L], reloaded.StationIds);
    }

    [Fact]
    public async Task The_stations_go_latest_first_and_saving_the_same_hours_again_changes_nothing()
    {
        var overrides = new StationHoursOverrides(_preferences, _time);
        await overrides.SaveAsync(12, OpeningHours.Parse(CostcoRule), "Costco", null);
        _time.Advance(TimeSpan.FromMinutes(1));
        await overrides.SaveAsync(7, OpeningHours.AlwaysOpen, "Shell", null);
        var changes = 0;
        overrides.Changed += () => changes++;

        _time.Advance(TimeSpan.FromMinutes(1));
        await overrides.SaveAsync(12, OpeningHours.Parse(CostcoRule), "Costco", null);

        Assert.Equal(0, changes);
        Assert.Equal([7L, 12L], overrides.StationIds);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Unreadable_storage_starts_empty(string saved)
    {
        await _preferences.SetAsync(StationHoursOverrides.StorageKey, saved);
        var overrides = new StationHoursOverrides(_preferences, _time);

        await overrides.LoadAsync();

        Assert.Equal(0, overrides.Count);
    }

    [Fact]
    public async Task What_can_t_be_read_is_left_out_one_station_at_a_time()
    {
        await _preferences.SetAsync(StationHoursOverrides.StorageKey, """
            {"12":{"rule":"Mo-Fr 6:00-21:30, Sa-Su 06:00-20:30","savedAt":"2026-11-13T15:00:00Z","name":"Costco"},
             "13":{"rule":"sunrise-sunset","savedAt":"2026-11-13T15:00:00Z"},
             "14":{"savedAt":"2026-11-13T15:00:00Z"},
             "15":{"rule":"24/7","savedAt":5},
             "abc":{"rule":"24/7","savedAt":"2026-11-13T15:00:00Z"},
             "-3":{"rule":"24/7","savedAt":"2026-11-13T15:00:00Z"},
             "16":"24/7",
             "17":{"rule":"24/7","savedAt":"2026-11-13T15:00:00Z","later":"a newer version's field"}}
            """);
        var overrides = new StationHoursOverrides(_preferences, _time);
        var changes = 0;
        overrides.Changed += () => changes++;

        await overrides.LoadAsync();

        Assert.Equal(1, changes);
        Assert.Equal([12L, 17L], overrides.StationIds.Order());
        Assert.Equal(CostcoRule, overrides.Find(12)!.Rule);
        Assert.Equal("24/7", overrides.Find(17)!.Rule);
    }

    [Fact]
    public async Task Corrections_are_kept_per_server_as_station_ids_are()
    {
        var inner = new MemoryPreferences();
        var server = "example.com";
        var scoped = new ServerScopedPreferences(inner, () => server);
        await new StationHoursOverrides(scoped, _time).SaveAsync(12, OpeningHours.AlwaysOpen, null, null);

        server = "other.example.org";
        var other = new StationHoursOverrides(scoped, _time);
        await other.LoadAsync();

        Assert.Equal(0, other.Count);
        Assert.Contains(StationHoursOverrides.StorageKey, ServerScopedPreferences.ScopedKeys);
        Assert.NotNull(await inner.GetAsync("essence.hours@example.com"));
        Assert.Null(await inner.GetAsync(StationHoursOverrides.StorageKey));
    }

    [Fact]
    public async Task The_linked_OpenStreetMap_account_is_not_per_server()
    {
        var inner = new MemoryPreferences();
        var scoped = new ServerScopedPreferences(inner, () => "example.com");

        await new PreferenceOsmTokenStore(scoped).SetAsync("{}");

        Assert.Equal("{}", await inner.GetAsync("essence.osm"));
    }
}
