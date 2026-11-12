using Essence.Contracts;
using Essence.UI.Services;
using static Essence.UI.Tests.TestStations;

namespace Essence.UI.Tests;

public class FavoriteStationsTests
{
    private readonly MemoryPreferences _preferences = new();

    [Fact]
    public async Task Favorites_are_saved_and_read_back_in_order()
    {
        var favorites = new FavoriteStations(_preferences);
        await favorites.AddAsync(Station(3));
        await favorites.AddAsync(Station(1));

        var reloaded = new FavoriteStations(_preferences);
        await reloaded.LoadAsync();

        Assert.Equal([Station(3), Station(1)], reloaded.Favorites);
        Assert.True(reloaded.IsFavorite(1));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":1}")]
    [InlineData("")]
    public async Task Unreadable_storage_starts_empty(string saved)
    {
        await _preferences.SetAsync(FavoriteStations.StorageKey, saved);
        var favorites = new FavoriteStations(_preferences);

        await favorites.LoadAsync();

        Assert.Empty(favorites.Favorites);
    }

    [Fact]
    public async Task A_station_is_a_favorite_once()
    {
        var favorites = new FavoriteStations(_preferences);

        await favorites.AddAsync(Station(1));
        await favorites.AddAsync(Station(1));

        Assert.Equal(1, favorites.Count);
    }

    [Fact]
    public async Task Removing_a_favorite_is_saved()
    {
        var favorites = new FavoriteStations(_preferences);
        await favorites.AddAsync(Station(1));
        await favorites.AddAsync(Station(2));

        await favorites.RemoveAsync(1);

        var reloaded = new FavoriteStations(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal([Station(2)], reloaded.Favorites);
    }

    [Fact]
    public async Task Refresh_updates_the_saved_snapshot()
    {
        var favorites = new FavoriteStations(_preferences);
        await favorites.AddAsync(Station(1));

        await favorites.RefreshAsync(Detail(1, brand: "Esso"));
        await favorites.RefreshAsync(Detail(2, brand: "Esso")); // not a favorite: ignored

        var reloaded = new FavoriteStations(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal("Esso", Assert.Single(reloaded.Favorites).Brand);
    }

    [Fact]
    public async Task Refresh_keeps_the_station_s_latest_hours_for_its_row()
    {
        var favorites = new FavoriteStations(_preferences);
        await favorites.AddAsync(Station(1));
        var hours = new StationHours("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", "costco", Trusted: true, Confirmed: true, null, null);

        await favorites.RefreshAsync(Detail(1) with { Hours = hours });

        var reloaded = new FavoriteStations(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal(hours, Assert.Single(reloaded.Favorites).Hours);
    }

    [Fact]
    public async Task Favorites_saved_before_there_were_hours_still_read()
    {
        await _preferences.SetAsync(FavoriteStations.StorageKey, """
            [{"id":1,"brand":"Shell","name":"Shell 1","address":"1 rue Principale","region":"Montréal","latitude":45.5,"longitude":-73.56}]
            """);
        var favorites = new FavoriteStations(_preferences);

        await favorites.LoadAsync();

        Assert.Equal(Station(1), Assert.Single(favorites.Favorites));
    }

    [Fact]
    public async Task Changes_are_announced()
    {
        var favorites = new FavoriteStations(_preferences);
        var changes = 0;
        favorites.Changed += () => changes++;

        await favorites.AddAsync(Station(1));
        await favorites.AddAsync(Station(1)); // already there
        await favorites.RefreshAsync(Detail(1)); // unchanged
        await favorites.RemoveAsync(2); // not there
        await favorites.RemoveAsync(1);

        Assert.Equal(2, changes);
    }
}
