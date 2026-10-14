using Essence.UI.Services;
using static Essence.UI.Tests.TestStations;

namespace Essence.UI.Tests;

public class ComparedStationsTests
{
    private readonly MemoryPreferences _preferences = new();

    [Fact]
    public async Task Stations_take_the_first_free_color()
    {
        var comparison = new ComparedStations(_preferences);
        await comparison.AddAsync(Station(10));
        await comparison.AddAsync(Station(20));
        await comparison.AddAsync(Station(30));

        await comparison.RemoveAsync(20);
        await comparison.AddAsync(Station(40));

        Assert.Equal(0, comparison.ColorOf(10));
        Assert.Equal(1, comparison.ColorOf(40));
        Assert.Equal(2, comparison.ColorOf(30));
        Assert.Null(comparison.ColorOf(20));
        Assert.Equal([10, 40, 30], comparison.Stations.Select(c => c.Station.Id));
    }

    [Fact]
    public async Task The_comparison_and_its_colors_are_saved()
    {
        var comparison = new ComparedStations(_preferences);
        await comparison.AddAsync([Station(10), Station(20), Station(30)]);
        await comparison.RemoveAsync(20);

        var reloaded = new ComparedStations(_preferences);
        await reloaded.LoadAsync();

        Assert.Equal([new ComparedStation(Station(10), 0), new ComparedStation(Station(30), 2)], reloaded.Stations);
    }

    [Fact]
    public async Task It_holds_up_to_eight_stations()
    {
        var comparison = new ComparedStations(_preferences);

        var added = await comparison.AddAsync(Enumerable.Range(1, 10).Select(id => Station(id)));

        Assert.Equal(ComparedStations.MaxStations, added);
        Assert.True(comparison.IsFull);
        Assert.False(await comparison.AddAsync(Station(11)));
        Assert.True(await comparison.AddAsync(Station(1))); // already in
        Assert.False(comparison.Contains(9));
    }

    [Fact]
    public async Task Adding_many_skips_the_ones_already_in()
    {
        var comparison = new ComparedStations(_preferences);
        await comparison.AddAsync(Station(1));

        var added = await comparison.AddAsync([Station(1), Station(2)]);

        Assert.Equal(1, added);
        Assert.Equal(2, comparison.Count);
    }

    [Fact]
    public async Task Clearing_empties_the_saved_comparison()
    {
        var comparison = new ComparedStations(_preferences);
        await comparison.AddAsync([Station(1), Station(2)]);

        await comparison.ClearAsync();

        var reloaded = new ComparedStations(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal(0, comparison.Count);
        Assert.Empty(reloaded.Stations);
    }

    [Fact]
    public async Task Loading_ignores_duplicates_and_extra_slots()
    {
        var ids = new long?[] { 1, null, 1, 2, 3, 4, 5, 6, 7, 8 };
        var json = "[" + string.Join(",", ids.Select(id => id is { } i ? $"{{\"id\":{i},\"address\":\"a\",\"region\":\"r\"}}" : "null")) + "]";
        await _preferences.SetAsync(ComparedStations.StorageKey, json);
        var comparison = new ComparedStations(_preferences);

        await comparison.LoadAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6], comparison.Stations.Select(c => c.Station.Id));
        Assert.Equal(3, comparison.ColorOf(2));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task Unreadable_storage_starts_empty(string saved)
    {
        await _preferences.SetAsync(ComparedStations.StorageKey, saved);
        var comparison = new ComparedStations(_preferences);

        await comparison.LoadAsync();

        Assert.Equal(0, comparison.Count);
    }

    [Fact]
    public async Task Favorites_are_not_involved()
    {
        await new FavoriteStations(_preferences).AddAsync(Station(1));
        var comparison = new ComparedStations(_preferences);

        await comparison.AddAsync(Station(2));

        Assert.Equal([2], comparison.Stations.Select(c => c.Station.Id));
    }

    [Fact]
    public async Task Refresh_updates_the_saved_snapshot()
    {
        var comparison = new ComparedStations(_preferences);
        await comparison.AddAsync(Station(1));

        await comparison.RefreshAsync(Detail(1, brand: "Esso"));

        var reloaded = new ComparedStations(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal("Esso", Assert.Single(reloaded.Stations).Station.Brand);
    }

    [Fact]
    public async Task Changes_are_announced()
    {
        var comparison = new ComparedStations(_preferences);
        var changes = 0;
        comparison.Changed += () => changes++;

        await comparison.AddAsync(Station(1));
        await comparison.AddAsync(Station(1)); // already in
        await comparison.RemoveAsync(2); // not in
        await comparison.ClearAsync();
        await comparison.ClearAsync(); // already empty

        Assert.Equal(2, changes);
    }
}
