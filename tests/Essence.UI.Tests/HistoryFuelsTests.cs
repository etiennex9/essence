using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class HistoryFuelsTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(10);

    private readonly MemoryPreferences _preferences = new();

    [Fact]
    public void Only_the_map_fuel_is_shown_at_first()
    {
        var fuels = new HistoryFuels(_preferences);

        Assert.True(fuels.IsShown("Régulier", "Régulier"));
        Assert.False(fuels.IsShown("Diesel", "Régulier"));
        Assert.False(fuels.IsShown("Régulier", null));
    }

    [Fact]
    public async Task Added_fuels_are_saved()
    {
        var fuels = new HistoryFuels(_preferences);
        Assert.True(await fuels.ToggleAsync("Diesel", "Régulier"));
        await fuels.ToggleAsync("Super", "Régulier");

        var reloaded = await LoadedAsync();

        Assert.True(reloaded.IsShown("Diesel", "Régulier"));
        Assert.True(reloaded.IsShown("Super", "Régulier"));
    }

    [Fact]
    public async Task Removing_the_last_added_fuel_is_saved()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("Diesel", "Régulier");
        await fuels.ToggleAsync("Diesel", "Régulier");

        Assert.False(fuels.IsShown("Diesel", "Régulier"));
        Assert.False((await LoadedAsync()).IsShown("Diesel", "Régulier"));
    }

    [Fact]
    public async Task Toggling_before_loading_keeps_what_was_saved()
    {
        await _preferences.SetAsync(HistoryFuels.StorageKey, "Diesel");

        await new HistoryFuels(_preferences).ToggleAsync("Super", "Régulier");

        var reloaded = await LoadedAsync();
        Assert.True(reloaded.IsShown("Diesel", "Régulier"));
        Assert.True(reloaded.IsShown("Super", "Régulier"));
    }

    [Fact]
    public async Task Toggling_the_map_fuel_changes_nothing()
    {
        var fuels = new HistoryFuels(_preferences);

        Assert.False(await fuels.ToggleAsync("régulier", "Régulier"));

        Assert.False(fuels.IsShown("Régulier", "Diesel"));
        Assert.Null(await _preferences.GetAsync(HistoryFuels.StorageKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData("\nDiesel")]
    [InlineData("Diesel\n")]
    [InlineData("  Diesel  ")]
    public async Task Odd_saved_values_are_read_safely(string saved)
    {
        await _preferences.SetAsync(HistoryFuels.StorageKey, saved);

        var fuels = await LoadedAsync();

        Assert.Equal(saved.Contains("Diesel"), fuels.IsShown("Diesel", "Régulier"));
        Assert.False(fuels.IsShown("", "Régulier"));
    }

    [Fact]
    public async Task A_fuel_with_a_comma_in_its_name_is_saved_whole()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("Diesel, bio", "Régulier");

        var reloaded = await LoadedAsync();

        Assert.True(reloaded.IsShown("Diesel, bio", "Régulier"));
        Assert.False(reloaded.IsShown("Diesel", "Régulier"));
    }

    [Fact]
    public async Task Fuels_match_whatever_their_case()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("diesel", "Régulier");

        Assert.True(fuels.IsShown("Diesel", "Régulier"));
        Assert.True(fuels.IsShown("Régulier", "régulier"));
    }

    [Fact]
    public async Task A_fuel_saved_in_two_cases_is_removed_at_once()
    {
        await _preferences.SetAsync(HistoryFuels.StorageKey, "Diesel\ndiesel");

        await new HistoryFuels(_preferences).ToggleAsync("Diesel", "Régulier");

        Assert.False((await LoadedAsync()).IsShown("Diesel", "Régulier"));
    }

    [Fact]
    public async Task Added_fuels_stay_when_the_map_fuel_changes()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("Diesel", "Régulier");

        var filtered = fuels.Filter(Chart("Régulier", "Super", "Diesel"), "Super");

        Assert.Equal(["Super", "Diesel"], filtered.Series.Select(s => s.Key));
    }

    [Theory]
    [InlineData("Super", true)]
    [InlineData("super", true)]
    [InlineData("Régulier", false)]
    [InlineData(null, false)]
    public void Chips_apply_when_the_map_fuel_has_a_line_among_others(string? mapFuel, bool applies) =>
        Assert.Equal(applies, HistoryFuels.Applies(Chart("Super", "Diesel"), mapFuel));

    [Fact]
    public void Chips_dont_apply_to_a_single_line_or_none()
    {
        Assert.False(HistoryFuels.Applies(Chart("Régulier"), "Régulier"));
        Assert.False(HistoryFuels.Applies(Chart(), "Régulier"));
    }

    [Fact]
    public void Highlight_is_the_key_of_the_map_fuel_line()
    {
        Assert.Equal("Régulier", HistoryFuels.Highlight(Chart("Régulier", "Diesel"), "régulier"));
        Assert.Equal("Régulier", HistoryFuels.Highlight(Chart("Régulier"), "Régulier"));
        Assert.Null(HistoryFuels.Highlight(Chart("Super", "Diesel"), "Régulier"));
    }

    [Fact]
    public async Task Filter_keeps_the_map_fuel_and_the_added_ones()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("Diesel", "Régulier");

        var filtered = fuels.Filter(Chart("Régulier", "Super", "Diesel"), "Régulier");

        Assert.Equal(["Régulier", "Diesel"], filtered.Series.Select(s => s.Key));
        Assert.Equal([0, 2], filtered.Series.Select(s => s.Color));
    }

    [Fact]
    public async Task Filter_leaves_a_chart_it_does_not_apply_to_as_it_is()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("Diesel", "Régulier");
        var withoutMapFuel = Chart("Super", "Propane");
        var singleLine = Chart("Régulier");

        Assert.Same(withoutMapFuel, fuels.Filter(withoutMapFuel, "Régulier"));
        Assert.Same(singleLine, fuels.Filter(singleLine, "Régulier"));
    }

    [Fact]
    public async Task A_fuel_the_station_does_not_sell_changes_nothing()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.ToggleAsync("Diesel", "Régulier");

        var filtered = fuels.Filter(Chart("Régulier", "Super"), "Régulier");

        Assert.Equal(["Régulier"], filtered.Series.Select(s => s.Key));
    }

    private async Task<HistoryFuels> LoadedAsync()
    {
        var fuels = new HistoryFuels(_preferences);
        await fuels.LoadAsync();
        return fuels;
    }

    private static PriceChartData Chart(params string[] fuels) =>
        HistorySeries.Build(fuels.Select(fuel => new PriceHistoryDto(fuel, 160m, true, From, null)), From, To);
}
