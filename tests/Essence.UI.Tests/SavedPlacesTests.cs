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

    [Fact]
    public async Task A_place_keeps_its_address()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Work, Office, address: "  1000 Rue De La Gauchetière Ouest, Montréal ");

        var reloaded = new SavedPlaces(_preferences);
        await reloaded.LoadAsync();

        Assert.Equal("1000 Rue De La Gauchetière Ouest, Montréal", reloaded.Work!.Address);
    }

    [Fact]
    public async Task A_place_saved_before_addresses_loads_without_one_and_gets_it_later()
    {
        await _preferences.SetAsync(SavedPlaces.StorageKey, """[{"id":"a","kind":"Home","name":"","latitude":45.5582,"longitude":-73.5512}]""");
        var places = new SavedPlaces(_preferences);
        await places.LoadAsync();
        var changes = 0;
        places.Changed += () => changes++;

        await places.SetAddressAsync("a", "6200 Rue Saint-Hubert, Montréal");
        await places.SetAddressAsync("a", "6200 Rue Saint-Hubert, Montréal");
        await places.SetAddressAsync("missing", "Nowhere");

        var reloaded = new SavedPlaces(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal("6200 Rue Saint-Hubert, Montréal", reloaded.Home!.Address);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_blank_address_is_none_and_a_long_one_is_cut()
    {
        var places = new SavedPlaces(_preferences);

        var blank = await places.SaveAsync(PlaceKind.Other, House, "Gym", "   ");
        var long_ = await places.SaveAsync(PlaceKind.Other, Office, "Office", new string('a', 200));

        Assert.Null(blank.Address);
        Assert.Equal(SavedPlace.MaxAddressLength, long_.Address!.Length);
    }

    [Fact]
    public async Task A_named_place_can_be_renamed()
    {
        var places = new SavedPlaces(_preferences);
        var cottage = await places.SaveAsync(PlaceKind.Other, House, "Cottage");
        var home = await places.SaveAsync(PlaceKind.Home, Office);
        var changes = 0;
        places.Changed += () => changes++;

        await places.RenameAsync(cottage.Id, $"  Garderie {new string('x', 50)}");
        await places.RenameAsync(cottage.Id, "   ");
        await places.RenameAsync(home.Id, "Castle");
        await places.RenameAsync("missing", "Nowhere");

        var reloaded = new SavedPlaces(_preferences);
        await reloaded.LoadAsync();
        var renamed = reloaded.Places.Single(p => p.Id == cottage.Id);
        Assert.StartsWith("Garderie x", renamed.Name);
        Assert.Equal(SavedPlace.MaxNameLength, renamed.Name.Length);
        Assert.Equal("", reloaded.Home!.Name);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_forgotten_place_comes_back_where_it_was()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Other, House, "Gym");
        var cottage = await places.SaveAsync(PlaceKind.Other, House, "Cottage", "1 Chemin du Lac");
        await places.SaveAsync(PlaceKind.Other, House, "School");

        var removed = await places.RemoveAsync(cottage.Id);
        await places.RestoreAsync(removed!);
        await places.RestoreAsync(removed!);

        var reloaded = new SavedPlaces(_preferences);
        await reloaded.LoadAsync();
        Assert.Equal(["Gym", "Cottage", "School"], reloaded.Places.Select(p => p.Name));
        Assert.Equal(cottage, reloaded.Places[1]);
        Assert.Null(await places.RemoveAsync("missing"));
    }

    [Fact]
    public async Task A_home_brought_back_replaces_the_one_saved_since()
    {
        var places = new SavedPlaces(_preferences);
        var home = await places.SaveAsync(PlaceKind.Home, House);
        var removed = await places.RemoveAsync(home.Id);
        await places.SaveAsync(PlaceKind.Home, Office);

        await places.RestoreAsync(removed!);

        Assert.Equal(home, Assert.Single(places.Places));
    }

    [Fact]
    public async Task A_place_brought_back_past_the_limit_makes_the_oldest_go()
    {
        var places = new SavedPlaces(_preferences);
        var first = await places.SaveAsync(PlaceKind.Other, Office, "Place 0");
        var removed = await places.RemoveAsync(first.Id);
        for (var i = 1; i <= SavedPlaces.MaxPlaces; i++)
        {
            await places.SaveAsync(PlaceKind.Other, Office, $"Place {i}");
        }

        await places.RestoreAsync(removed!);

        Assert.Equal(SavedPlaces.MaxPlaces, places.Places.Count);
        Assert.Equal("Place 0", places.Places[0].Name);
        Assert.DoesNotContain(places.Places, p => p.Name == "Place 1");
    }
}
