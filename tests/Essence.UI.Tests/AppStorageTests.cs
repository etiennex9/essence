using System.Text;
using Essence.Contracts;
using Essence.UI.App;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class AppStorageTests
{
    [Fact]
    public async Task The_last_location_is_kept_with_its_time()
    {
        var preferences = new MemoryPreferences();
        var fix = new LastFix(45.5017, -73.5673, 25, new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(-5)));

        Assert.Null(await LastLocation.ReadAsync(preferences));
        await LastLocation.WriteAsync(preferences, fix);

        Assert.Equal(fix, await LastLocation.ReadAsync(preferences));
    }

    [Fact]
    public async Task A_damaged_last_location_reads_as_none()
    {
        var preferences = new MemoryPreferences();
        await preferences.SetAsync(LastLocation.StorageKey, "{not json");

        Assert.Null(await LastLocation.ReadAsync(preferences));
    }

    [Fact]
    public async Task Favorites_and_the_comparison_are_kept_per_server()
    {
        var inner = new MemoryPreferences();
        var server = "example.com";
        var scoped = new ServerScopedPreferences(inner, () => server);
        var favorites = new FavoriteStations(scoped);
        await favorites.AddAsync(TestStations.Station(12));

        server = "Other.example.org:8443";
        var otherFavorites = new FavoriteStations(scoped);
        await otherFavorites.LoadAsync();
        Assert.Equal(0, otherFavorites.Count);

        Assert.NotNull(await inner.GetAsync("essence.favorites@example.com"));
        Assert.Equal("essence.compare@other.example.org:8443", scoped.KeyFor(ComparedStations.StorageKey));
        Assert.Null(await inner.GetAsync(FavoriteStations.StorageKey));
    }

    [Fact]
    public async Task Cars_places_and_the_fuel_stay_shared()
    {
        var inner = new MemoryPreferences();
        var scoped = new ServerScopedPreferences(inner, () => "example.com");

        await scoped.SetAsync(Fuels.PreferenceKey, "Diesel");
        await scoped.SetAsync(CarProfiles.StorageKey, "[]");
        await scoped.SetAsync(SavedPlaces.StorageKey, "[]");

        Assert.Equal("Diesel", await inner.GetAsync(Fuels.PreferenceKey));
        Assert.Equal("[]", await inner.GetAsync(CarProfiles.StorageKey));
        Assert.Equal("[]", await inner.GetAsync(SavedPlaces.StorageKey));
    }

    [Fact]
    public void Before_a_server_is_chosen_keys_are_left_as_they_are()
    {
        var scoped = new ServerScopedPreferences(new MemoryPreferences(), () => null);

        Assert.Equal(FavoriteStations.StorageKey, scoped.KeyFor(FavoriteStations.StorageKey));
    }

    [Fact]
    public async Task Bundled_release_notes_read_like_the_web_app_s()
    {
        var files = new Dictionary<string, string>
        {
            ["release.json"] = """{"version":"2026-11-07","commit":"6d6e385","months":["2026-11"],"unreleased":[]}""",
            ["changelog-2026-11.json"] = """{"versions":[{"date":"2026-11-07","changes":[{"en":"Prices zoomed out","fr":"Prix vus de loin"}]}]}""",
        };
        var source = new FileReleaseNotesSource((name, _) =>
            Task.FromResult<Stream?>(files.TryGetValue(name, out var json) ? new MemoryStream(Encoding.UTF8.GetBytes(json)) : null));

        var release = await source.GetReleaseAsync(TestContext.Current.CancellationToken);
        var month = await source.GetMonthAsync("2026-11", TestContext.Current.CancellationToken);
        var missing = await source.GetMonthAsync("2026-10", TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 11, 7), release.Value!.Version);
        Assert.Equal("Prix vus de loin", Assert.Single(Assert.Single(month.Value!.Versions).Changes).Fr);
        Assert.Equal(ApiError.NotFound, missing.Error);
    }

    [Fact]
    public async Task Without_bundled_notes_it_is_a_development_version()
    {
        var notes = new ReleaseNotes(new FileReleaseNotesSource((_, _) => Task.FromResult<Stream?>(null)));

        await notes.LoadReleaseAsync();

        Assert.Equal(ReleaseState.Missing, notes.State);
    }

    [Fact]
    public async Task Damaged_bundled_notes_fail_without_throwing()
    {
        var source = new FileReleaseNotesSource((_, _) => Task.FromResult<Stream?>(new MemoryStream("{oops"u8.ToArray())));

        var release = await source.GetReleaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApiError.Failed, release.Error);
    }
}
