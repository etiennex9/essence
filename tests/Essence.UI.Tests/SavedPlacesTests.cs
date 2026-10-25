using Essence.UI.Services;

namespace Essence.UI.Tests;

public class SavedPlacesTests
{
    private static readonly GeoPoint Office = new(45.4995, -73.5662);
    private static readonly GeoPoint House = new(45.5582, -73.5512);

    private readonly MemoryPreferences _preferences = new();

    [Fact]
    public async Task Home_work_and_named_places_are_kept_on_the_device()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Other, new GeoPoint(46.81, -71.21), "  Cottage  ");
        await places.SaveAsync(PlaceKind.Work, Office, "ignored");
        await places.SaveAsync(PlaceKind.Home, House);

        var reloaded = new SavedPlaces(_preferences);
        await reloaded.LoadAsync();

        Assert.Equal([PlaceKind.Home, PlaceKind.Work, PlaceKind.Other], reloaded.Places.Select(p => p.Kind));
        Assert.Equal(House, reloaded.Home!.Point);
        Assert.Equal(Office, reloaded.Work!.Point);
        Assert.Equal("", reloaded.Work.Name);
        Assert.Equal("Cottage", reloaded.Places[2].Name);
    }

    [Fact]
    public async Task Saving_work_again_replaces_it()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Work, Office);

        await places.SaveAsync(PlaceKind.Work, House);

        Assert.Equal(House, Assert.Single(places.Places).Point);
    }

    [Fact]
    public async Task A_place_can_be_forgotten()
    {
        var places = new SavedPlaces(_preferences);
        var cottage = await places.SaveAsync(PlaceKind.Other, House, "Cottage");
        var changes = 0;
        places.Changed += () => changes++;

        await places.RemoveAsync(cottage.Id);
        await places.RemoveAsync(cottage.Id);

        Assert.Empty(places.Places);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Past_the_limit_the_oldest_named_place_goes()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, House);
        for (var i = 0; i < SavedPlaces.MaxPlaces; i++)
        {
            await places.SaveAsync(PlaceKind.Other, Office, $"Place {i}");
        }

        Assert.Equal(SavedPlaces.MaxPlaces, places.Places.Count);
        Assert.NotNull(places.Home);
        Assert.DoesNotContain(places.Places, p => p.Name == "Place 0");
        Assert.Contains(places.Places, p => p.Name == $"Place {SavedPlaces.MaxPlaces - 1}");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""[{"id":"a","kind":"Gym","name":"","latitude":45,"longitude":-73}]""")]
    public async Task Unreadable_places_are_none(string json)
    {
        await _preferences.SetAsync(SavedPlaces.StorageKey, json);
        var places = new SavedPlaces(_preferences);

        await places.LoadAsync();

        Assert.Empty(places.Places);
    }

    [Fact]
    public async Task Places_out_of_range_or_a_second_home_are_dropped()
    {
        await _preferences.SetAsync(SavedPlaces.StorageKey, """
            [{"id":"a","kind":"Home","name":"","latitude":45.5,"longitude":-73.5},
             {"id":"b","kind":"Home","name":"","latitude":45.6,"longitude":-73.6},
             {"id":"c","kind":"Other","name":"Moon","latitude":95,"longitude":-73.5},
             null]
            """);
        var places = new SavedPlaces(_preferences);

        await places.LoadAsync();

        Assert.Equal("a", Assert.Single(places.Places).Id);
    }
}
