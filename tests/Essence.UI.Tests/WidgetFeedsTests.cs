using System.Globalization;
using System.Net;
using Essence.UI.App;
using Essence.UI.App.Widgets;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public sealed class WidgetFeedsTests : IDisposable
{
    private static readonly GeoPoint HomePoint = new(45.5017, -73.5673);
    private static readonly GeoPoint WorkPoint = new(45.4995, -73.5662);

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 13, 10, 0, TimeSpan.Zero));
    private readonly MemoryPreferences _preferences = new();
    private readonly Dictionary<string, HttpResponseMessage> _answers = [];
    private readonly TestHttp _http;
    private readonly WidgetFeeds _feeds;

    public WidgetFeedsTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-CA");
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/Toronto"));
        _http = new TestHttp(r => _answers.Remove(r.RequestUri!.PathAndQuery, out var answer) ? answer : TestHttp.Status(HttpStatusCode.NotFound));
        var l = new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        _feeds = new WidgetFeeds(_preferences, new EssenceApiClient(new HttpClient(_http) { BaseAddress = new Uri("http://api.test/") }), l, _time);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Fact]
    public async Task Best_near_a_place_asks_what_best_near_you_asks_for_the_car_driven()
    {
        await _preferences.SetAsync(Fuels.PreferenceKey, "Super");
        await new CarProfiles(_preferences).SaveAsync(new CarProfile(CarProfiles.TypicalId, "Mazda", "Super", 45, 7.2m, true, 40, 15));
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer("/stations/best?lat=45.5017&lon=-73.5673&fuel=Super&fillL=45&per100Km=7.2&costco=true&maxDetourMin=15", Best(pick: 2, reference: 1, "3.10"));

        var view = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.None, view.Problem);
        Assert.Equal("Best near Home", view.Title);
        Assert.Equal("Super", view.Fuel);
        var line = Assert.Single(view.Lines);
        Assert.Equal(("Shell", "2.0 km · Saves $3.10", "166.9", "/?station=2"), (line.Name, line.Detail, line.Price, line.Tap));
        Assert.Equal($"Updated {Formatting.TimeOfDay(_time.GetUtcNow(), _time.LocalTimeZone, CultureInfo.CurrentCulture)}", view.Updated);
    }

    [Fact]
    public async Task The_closest_is_said_to_be_the_closest()
    {
        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 20, _time.GetUtcNow()));
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));

        var view = await _feeds.BestNearAsync(WidgetPlaceRef.LastUsed, TestContext.Current.CancellationToken);

        Assert.Equal("Best near you", view.Title);
        Assert.Equal("1.0 km · Closest", Assert.Single(view.Lines).Detail);
    }

    [Fact]
    public async Task Home_is_found_after_it_s_saved_again()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, WorkPoint);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));

        var view = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.None, view.Problem);
    }

    [Fact]
    public async Task A_forgotten_place_or_no_location_yet_says_so_without_asking()
    {
        var removed = await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Other, WorkPoint, "Gym");
        await new SavedPlaces(_preferences).RemoveAsync(removed.Id);

        var gone = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Other, removed.Id), TestContext.Current.CancellationToken);
        var nowhere = await _feeds.BestNearAsync(WidgetPlaceRef.LastUsed, TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.NoPlace, gone.Problem);
        Assert.Equal(WidgetProblem.NoLocationYet, nowhere.Problem);
        Assert.NotEmpty(nowhere.Message!);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, WidgetProblem.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, WidgetProblem.Failed)]
    public async Task A_server_problem_is_a_failure_to_try_again(HttpStatusCode status, WidgetProblem problem)
    {
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(BestPath(HomePoint), TestHttp.Status(status));

        var view = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal(problem, view.Problem);
        Assert.Empty(view.Lines);
    }

    [Fact]
    public async Task No_price_nearby_names_the_fuel()
    {
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(BestPath(HomePoint), TestHttp.Json("""{"pick":null,"referenceId":null,"closestId":null,"cheapestId":null,"stations":[],"radiusKm":null,"byRoad":false}"""));

        var view = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.NoPrice, view.Problem);
        Assert.Contains("Régulier", view.Message);
    }

    [Fact]
    public async Task Favorites_are_cheapest_first_and_dropped_stations_are_left_out()
    {
        var favorites = new FavoriteStations(_preferences);
        foreach (var id in new long[] { 1, 2, 3, 4 })
        {
            await favorites.AddAsync(TestStations.Station(id, id == 3 ? "Esso" : "Shell"));
        }

        Answer("/stations/1", Station(1, "170.9"));
        Answer("/stations/2", Station(2, null));
        Answer("/stations/3", Station(3, "159.9"));
        // 4 is gone from the feed: 404.

        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Favorites", view.Title);
        Assert.Equal([3L, 1L, 2L], view.Lines.Select(l => l.StationId));
        Assert.Equal(["159.9", "170.9", "—"], view.Lines.Select(l => l.Price));
        Assert.Equal(("Esso", "3 rue Principale", "/?station=3"), (view.Lines[0].Name, view.Lines[0].Detail, view.Lines[0].Tap));
        Assert.Equal("/?view=favorites", view.Tap);
    }

    [Fact]
    public async Task One_favorite_unanswered_fails_the_refresh_rather_than_show_it_without_a_price()
    {
        var favorites = new FavoriteStations(_preferences);
        await favorites.AddAsync(TestStations.Station(1));
        await favorites.AddAsync(TestStations.Station(2));
        Answer("/stations/1", Station(1, "170.9"));
        Answer("/stations/2", TestHttp.Status(HttpStatusCode.TooManyRequests));

        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.Failed, view.Problem);
    }

    [Fact]
    public async Task A_failed_refresh_shows_the_last_view_with_the_failure_in_its_footer()
    {
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(BestPath(HomePoint), Best(pick: 2, reference: 1, "3.10"));
        var good = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);
        await WidgetViewCache.WriteAsync(_preferences, 7, good);
        Answer(BestPath(HomePoint), TestHttp.Status(HttpStatusCode.ServiceUnavailable));
        var failure = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        var shown = WidgetViewCache.Failed(await WidgetViewCache.ReadAsync(_preferences, 7), failure);
        var neverShown = WidgetViewCache.Failed(await WidgetViewCache.ReadAsync(_preferences, 8), failure);

        Assert.Equal(good.Lines, shown.Lines);
        Assert.Equal(failure.Message, shown.Updated);
        Assert.Equal(failure, neverShown);
    }

    [Fact]
    public async Task At_most_five_favorites_show()
    {
        var favorites = new FavoriteStations(_preferences);
        for (var id = 1; id <= 8; id++)
        {
            await favorites.AddAsync(TestStations.Station(id));
            Answer($"/stations/{id}", Station(id, "165.0"));
        }

        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], view.Lines.Select(l => l.StationId));
    }

    [Fact]
    public async Task No_favorites_says_how_to_add_some()
    {
        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.NoFavorites, view.Problem);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Favorites_from_another_server_are_not_this_one_s()
    {
        var server = "a.example.com";
        var scoped = new ServerScopedPreferences(_preferences, () => server);
        await new FavoriteStations(scoped).AddAsync(TestStations.Station(1));
        server = "b.example.com";
        var feeds = new WidgetFeeds(scoped, new EssenceApiClient(new HttpClient(_http) { BaseAddress = new Uri("http://api.test/") }),
            new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)),
            _time);

        var view = await feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.NoFavorites, view.Problem);
    }

    [Fact]
    public async Task A_route_asks_without_the_range_and_shows_the_pick()
    {
        var cars = new CarProfiles(_preferences);
        await cars.SaveAsync(new CarProfile(CarProfiles.TypicalId, "Corolla", "Régulier", 40, 8.5m, false, 30, 10, RangeKm: 120, RangeAt: _time.GetUtcNow()));
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        await places.SaveAsync(PlaceKind.Work, WorkPoint);
        Answer(
            "/trips/best?fromLat=45.5017&fromLon=-73.5673&toLat=45.4995&toLon=-73.5662&fuel=R%C3%A9gulier&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&spareKm=30",
            Trip(pick: 3, reference: 5, detourKm: 2.5, detourMin: 4, saves: "1.26"));

        var view = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), new WidgetPlaceRef(PlaceKind.Work), TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.None, view.Problem);
        Assert.Equal("Home → Work", view.Title);
        var line = Assert.Single(view.Lines);
        // The tap opens this very trip: from Home, to Work, with this station on top, wherever the user is.
        Assert.Equal(("Petro-Canada", "+4 min · Saves $1.26", "159.9", "/?to=work&from=home&pick=3"), (line.Name, line.Detail, line.Price, line.Tap));
        Assert.DoesNotContain("rangeKm", Assert.Single(_http.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task A_pick_on_the_way_says_no_detour()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        var gym = await places.SaveAsync(PlaceKind.Other, WorkPoint, "Gym");
        Answer(TripPath(HomePoint, WorkPoint), Trip(pick: 3, reference: 3, detourKm: 0.4, detourMin: 1, saves: "0"));

        var view = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), WidgetPlaceRef.For(gym), TestContext.Current.CancellationToken);

        Assert.Equal("Home → Gym", view.Title);
        Assert.Equal(("No detour", $"/?to={gym.Id}&from=home&pick=3"), (Assert.Single(view.Lines).Detail, view.Tap));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, WidgetProblem.NoRoute)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WidgetProblem.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, WidgetProblem.Failed)]
    public async Task A_route_s_problems_are_told_apart(HttpStatusCode status, WidgetProblem problem)
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        await places.SaveAsync(PlaceKind.Work, WorkPoint);
        Answer(TripPath(HomePoint, WorkPoint), TestHttp.Status(status));

        var view = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), new WidgetPlaceRef(PlaceKind.Work), TestContext.Current.CancellationToken);

        Assert.Equal(problem, view.Problem);
    }

    [Fact]
    public async Task A_route_from_where_the_app_was_last_used_starts_from_where_the_user_is()
    {
        await LastLocation.WriteAsync(_preferences, new LastFix(WorkPoint.Latitude, WorkPoint.Longitude, 20, _time.GetUtcNow()));
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(TripPath(WorkPoint, HomePoint), Trip(pick: 3, reference: 3, detourKm: 0.4, detourMin: 1, saves: "0"));

        var view = await _feeds.TripAsync(WidgetPlaceRef.LastUsed, new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal("/?to=home&pick=3", Assert.Single(view.Lines).Tap);
    }

    [Fact]
    public async Task A_route_without_its_places_asks_for_them()
    {
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);

        var noRoute = await _feeds.TripAsync(null, null, TestContext.Current.CancellationToken);
        var noWork = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), new WidgetPlaceRef(PlaceKind.Work), TestContext.Current.CancellationToken);

        Assert.Equal((WidgetProblem.NoPlace, "/?view=places"), (noRoute.Problem, noRoute.Tap));
        Assert.Equal((WidgetProblem.NoPlace, "/?view=places"), (noWork.Problem, noWork.Tap));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_widget_s_settings_are_kept_by_its_id()
    {
        var config = new WidgetConfig(From: new WidgetPlaceRef(PlaceKind.Home), To: new WidgetPlaceRef(PlaceKind.Other, "abc123"));

        await WidgetConfigs.WriteAsync(_preferences, 42, config);

        Assert.Equal(config, await WidgetConfigs.ReadAsync(_preferences, 42));
        Assert.Equal(new WidgetConfig(), await WidgetConfigs.ReadAsync(_preferences, 7));
    }

    [Fact]
    public void French_widgets_read_in_french()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");
        var l = new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var feeds = new WidgetFeeds(_preferences, new EssenceApiClient(new HttpClient(_http)), l, _time);

        Assert.Equal("Maison", feeds.PlaceName(new WidgetPlaceRef(PlaceKind.Home), new SavedPlaces(_preferences)));
        Assert.Equal("Ouvrez Essence une fois pour qu’il sache où vous êtes.", feeds.MessageFor(WidgetProblem.NoLocationYet, "Régulier"));
    }

    private void Answer(string pathAndQuery, HttpResponseMessage response) => _answers[pathAndQuery] = response;

    private static string BestPath(GeoPoint at) => string.Create(
        CultureInfo.InvariantCulture,
        $"/stations/best?lat={at.Latitude:0.######}&lon={at.Longitude:0.######}&fuel=R%C3%A9gulier&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10");

    private static string TripPath(GeoPoint from, GeoPoint to) => string.Create(
        CultureInfo.InvariantCulture,
        $"/trips/best?fromLat={from.Latitude:0.######}&fromLon={from.Longitude:0.######}&toLat={to.Latitude:0.######}&toLon={to.Longitude:0.######}&fuel=R%C3%A9gulier&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&spareKm=30");

    private static HttpResponseMessage Best(long pick, long reference, string saves) => TestHttp.Json($$"""
        {"pick":{"id":{{pick}},"name":null,"brand":"Shell","address":"{{pick}} rue Principale","latitude":45.5,"longitude":-73.56,
          "priceCents":166.9,"distanceKm":{{pick}},"durationMin":{{2 * pick}},"costDollars":66.99,"savesDollars":{{saves}},
          "membersOnly":false,"overDetourLimit":false,"savingUncertain":false},
         "referenceId":{{reference}},"closestId":1,"cheapestId":{{pick}},"stations":[],"radiusKm":10,"byRoad":true}
        """);

    private static HttpResponseMessage Station(long id, string? cents) => TestHttp.Json($$"""
        {"id":{{id}},"name":null,"brand":"Shell","status":"En opération","address":"{{id}} rue Principale","postalCode":"H2X3J4",
         "region":"Montréal","latitude":45.5,"longitude":-73.56,"isActive":true,"firstSeenAt":"2026-10-01T00:00:00Z",
         "lastSeenAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-08T00:00:00Z",
         "prices":[{"fuel":"Régulier","priceCents":{{cents ?? "null"}},"isAvailable":{{(cents is null ? "false" : "true")}},"since":"2026-11-08T10:00:00Z"},
                   {"fuel":"Diesel","priceCents":150.0,"isAvailable":true,"since":"2026-11-08T10:00:00Z"}]}
        """);

    private static HttpResponseMessage Trip(long pick, long reference, double detourKm, double detourMin, string saves) =>
        TripJson(pick, reference, detourKm.ToString(CultureInfo.InvariantCulture), detourMin.ToString(CultureInfo.InvariantCulture), saves);

    private static HttpResponseMessage TripJson(long pick, long reference, string detourKm, string detourMin, string saves) => TestHttp.Json($$"""
        {"path":[[-73.5673,45.5017],[-73.5662,45.4995]],"distanceKm":12.3,"durationMin":18,"halfWidthKm":2,"longTrip":false,
         "stopBeforeKm":null,"lowFuel":false,
         "stations":[{"id":{{pick}},"name":null,"brand":"Petro-Canada","address":"1 boul. Saint-Joseph","latitude":45.5,"longitude":-73.56,
           "priceCents":159.9,"kmAlong":5,"distanceKm":5,"detourKm":{{detourKm}},"detourMin":{{detourMin}},"costDollars":64.50,
           "savesDollars":{{saves}},"membersOnly":false,"reachable":true,"overDetourLimit":false}],
         "pickId":{{pick}},"referenceId":{{reference}},"referenceOnTheWay":true,"biggestSavingId":null}
        """);
}
