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

    /// <summary>About 6 km from Home and Work.</summary>
    private static readonly GeoPoint FarPoint = new(45.5500, -73.6100);

    /// <summary>Costco Laval's gas bar's hours, as the API sends them.</summary>
    private const string CostcoHours = """
        {"rule":"Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30","source":"costco","trusted":true,"confirmed":true,"checkedAt":null,
         "link":null,"temporarilyClosed":false}
        """;

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

        var view = await _feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.Equal("Best near you", view.Title);
        Assert.Equal("1.0 km · Closest", Assert.Single(view.Lines).Detail);
        // A fresh, precise fix: nothing to say about it.
        Assert.Equal(Updated(), view.Updated);
    }

    [Fact]
    public async Task An_old_fix_says_when_it_was_taken()
    {
        var at = _time.GetUtcNow().AddHours(-2);
        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 20, at));
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));

        var view = await _feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.Equal($"{Updated()} · Location as of {TimeOfDay(at)}", view.Updated);
    }

    [Fact]
    public async Task A_fix_from_another_day_gives_its_day()
    {
        var at = _time.GetUtcNow().AddDays(-2);
        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 20, at));
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));

        var view = await _feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.Equal($"{Updated()} · Location as of {Formatting.MonthDay(at, _time.LocalTimeZone, CultureInfo.CurrentCulture)}", view.Updated);
    }

    [Fact]
    public async Task An_approximate_fix_says_so()
    {
        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 2500, _time.GetUtcNow().AddMinutes(-5)));
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));

        var view = await _feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.Equal($"{Updated()} · approximate location", view.Updated);
    }

    [Fact]
    public async Task A_saved_place_says_nothing_of_the_location()
    {
        await LastLocation.WriteAsync(_preferences, new LastFix(FarPoint.Latitude, FarPoint.Longitude, 2500, _time.GetUtcNow().AddDays(-3)));
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));

        var view = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal(Updated(), view.Updated);
    }

    [Fact]
    public async Task Without_the_permission_a_kept_fix_still_ranks_and_without_one_it_asks_for_it()
    {
        var feeds = FeedsWith(WidgetLocationAccess.NotAllowed);

        var nowhere = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);
        Assert.Equal((WidgetProblem.LocationNotAllowed, "Open Essence and allow your location."), (nowhere.Problem, nowhere.Message));
        Assert.Empty(_http.Requests);

        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 20, _time.GetUtcNow().AddHours(-1)));
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));
        var kept = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);
        Assert.Equal(WidgetProblem.None, kept.Problem);
        Assert.Contains("Location as of", kept.Updated);
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
        var nowhere = await _feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.NoPlace, gone.Problem);
        Assert.Equal((WidgetProblem.NoLocationYet, "Tap ↻ to find where you are."), (nowhere.Problem, nowhere.Message));
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

    [Theory]
    [InlineData(20, 30, "Closes 9:30 p.m. · 1 rue Principale")]
    [InlineData(21, 45, "Closed · opens 6:00 a.m. · 1 rue Principale")]
    [InlineData(15, 0, "1 rue Principale")]
    public async Task A_favorite_says_when_it_closes_within_90_minutes_or_that_it_s_closed(int hour, int minute, string detail)
    {
        // Wednesday, Laval's time; the device's zone is another one, so the station's own is used.
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 11, hour, minute, 0, TimeSpan.FromHours(-5)));
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        await new FavoriteStations(_preferences).AddAsync(TestStations.Station(1, "Costco"));
        Answer("/stations/1", Station(1, "155.9", CostcoHours));

        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(detail, Plain(Assert.Single(view.Lines).Detail));
    }

    [Theory]
    [InlineData(20, 15, 8, "2.0 km · Saves $3.10 · Closes 9:30 p.m.")]
    [InlineData(21, 25, 10, "2.0 km · Saves $3.10 · Closes 9:30 p.m.")]
    [InlineData(21, 45, 8, "2.0 km · Saves $3.10 · Closed · opens 6:00 a.m.")]
    [InlineData(15, 0, 8, "2.0 km · Saves $3.10")]
    [InlineData(5, 50, 20, "2.0 km · Saves $3.10")]
    public async Task Best_near_a_place_says_when_its_pick_closes_or_that_it_s_closed_when_you_get_there(
        int hour, int minute, double etaMinutes, string detail)
    {
        // Wednesday, Laval's time, on a device in another zone. At 5:50, 20 minutes away, it's open by the time you get there.
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 11, hour, minute, 0, TimeSpan.FromHours(-5)));
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(BestPath(HomePoint), Best(pick: 2, reference: 1, "3.10", $$""","hours":{{CostcoHours}},"etaMin":{{etaMinutes}}"""));

        var view = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal(detail, Plain(Assert.Single(view.Lines).Detail));
    }

    [Theory]
    [InlineData("en-CA", 19, 30, 130, "No detour · Saves $1.26 · Closes 9:30 p.m.")]
    [InlineData("fr-CA", 19, 30, 130, "Sans détour · Économie de 1,26 $ · Ferme à 21 h 30")]
    [InlineData("en-CA", 5, 0, 90, "No detour · Saves $1.26")]
    [InlineData("en-CA", 5, 0, 30, "No detour · Saves $1.26 · Closed · opens 6:00 a.m.")]
    public async Task Best_on_a_frequent_route_counts_the_time_to_the_station(
        string culture, int hour, int minute, double toMinutes, string detail)
    {
        // 7:30 p.m. and 130 minutes to it: it closes on the way, more than 90 minutes after the refresh. At 5:00 a.m. and 90
        // minutes, it's open when the car gets there: nothing to say, though it's closed now.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 11, hour, minute, 0, TimeSpan.FromHours(-5)));
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        await places.SaveAsync(PlaceKind.Work, FarPoint);
        Answer(TripPath(HomePoint, FarPoint), TripJson(
            pick: 3, reference: 1, "0", "0", "1.26", $$""","hours":{{CostcoHours}},"toMin":{{toMinutes}}"""));

        var view = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), new WidgetPlaceRef(PlaceKind.Work), TestContext.Current.CancellationToken);

        Assert.Equal(detail, Plain(Assert.Single(view.Lines).Detail));
    }

    [Fact]
    public async Task A_favorite_s_hours_corrected_on_the_device_are_the_ones_said()
    {
        // 9:45 p.m.: Costco's hours say it's closed, the user's say it's open until 11.
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 11, 21, 45, 0, TimeSpan.FromHours(-5)));
        await new FavoriteStations(_preferences).AddAsync(TestStations.Station(1, "Costco"));
        await new StationHoursOverrides(_preferences, _time).SaveAsync(1, Essence.Hours.OpeningHours.Parse("Mo-Su 06:00-23:00"), "Costco", null);
        Answer("/stations/1", Station(1, "155.9", CostcoHours));

        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Closes 11:00 p.m. · 1 rue Principale", Plain(Assert.Single(view.Lines).Detail));
    }

    [Fact]
    public async Task Best_near_a_place_and_a_frequent_route_send_the_corrected_stations_and_say_their_hours()
    {
        // The user says the pick closes at 9:30; the server, without hours for it, never left it out.
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 11, 21, 15, 0, TimeSpan.FromHours(-5)));
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        await places.SaveAsync(PlaceKind.Work, FarPoint);
        await new StationHoursOverrides(_preferences, _time).SaveAsync(2, Essence.Hours.OpeningHours.Parse("Mo-Su 06:00-21:30"), "Shell", null);
        await new StationHoursOverrides(_preferences, _time).SaveAsync(3, Essence.Hours.OpeningHours.Parse("Mo-Su 06:00-21:30"), "Shell", null);
        Answer(BestPath(HomePoint) + "&ignoreHours=2,3", Best(pick: 2, reference: 1, "3.10", ""","etaMin":8"""));
        Answer(TripPath(HomePoint, FarPoint) + "&ignoreHours=2,3", TripJson(pick: 3, reference: 1, "0", "0", "1.26", ""","toMin":5"""));

        var best = await _feeds.BestNearAsync(new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);
        var trip = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), new WidgetPlaceRef(PlaceKind.Work), TestContext.Current.CancellationToken);

        Assert.Equal("2.0 km · Saves $3.10 · Closes 9:30 p.m.", Plain(Assert.Single(best.Lines).Detail));
        Assert.Equal("No detour · Saves $1.26 · Closes 9:30 p.m.", Plain(Assert.Single(trip.Lines).Detail));
    }

    [Fact]
    public async Task A_favorite_closed_for_now_says_so_in_french()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");
        await new FavoriteStations(_preferences).AddAsync(TestStations.Station(1, "Costco"));
        Answer("/stations/1", Station(1, "155.9", CostcoHours.Replace("\"temporarilyClosed\":false", "\"temporarilyClosed\":true")));

        var view = await _feeds.FavoritesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Fermé temporairement · 1 rue Principale", Assert.Single(view.Lines).Detail);
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
    public async Task With_the_location_off_the_widget_says_to_turn_it_on()
    {
        var feeds = FeedsWith(WidgetLocationAccess.WhileInUse, locationOn: false);

        var nowhere = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);
        Assert.Equal((WidgetProblem.LocationOff, "Turn on location, then tap ↻."), (nowhere.Problem, nowhere.Message));
        Assert.Empty(_http.Requests);

        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 20, _time.GetUtcNow()));
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));
        var kept = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);
        Assert.Equal(WidgetProblem.None, kept.Problem);
        Assert.Equal($"{Updated()} · location off", kept.Updated);
    }

    [Fact]
    public async Task Without_the_permission_the_location_setting_isn_t_the_problem()
    {
        var feeds = FeedsWith(WidgetLocationAccess.NotAllowed, locationOn: false);

        var view = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.LocationNotAllowed, view.Problem);
    }

    [Fact]
    public async Task A_route_from_where_you_are_starts_from_where_the_user_is()
    {
        var at = _time.GetUtcNow().AddHours(-1);
        await LastLocation.WriteAsync(_preferences, new LastFix(FarPoint.Latitude, FarPoint.Longitude, 20, at));
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);
        Answer(TripPath(FarPoint, HomePoint), Trip(pick: 3, reference: 3, detourKm: 0.4, detourMin: 1, saves: "0"));

        var view = await _feeds.TripAsync(WidgetPlaceRef.You, new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal("you → Home", view.Title);
        Assert.Equal("/?to=home&pick=3", Assert.Single(view.Lines).Tap);
        Assert.Equal($"{Updated()} · Location as of {TimeOfDay(at)}", view.Updated);
    }

    [Fact]
    public async Task A_route_from_where_you_are_at_its_destination_is_there_without_asking()
    {
        // Work is about 250 m from Home.
        await LastLocation.WriteAsync(_preferences, new LastFix(WorkPoint.Latitude, WorkPoint.Longitude, 20, _time.GetUtcNow()));
        await new SavedPlaces(_preferences).SaveAsync(PlaceKind.Home, HomePoint);

        var view = await _feeds.TripAsync(WidgetPlaceRef.You, new WidgetPlaceRef(PlaceKind.Home), TestContext.Current.CancellationToken);

        Assert.Equal((WidgetProblem.AtDestination, "You’re there.", "/?view=best"), (view.Problem, view.Message, view.Tap));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_route_between_two_saved_places_close_together_is_still_asked()
    {
        var places = new SavedPlaces(_preferences);
        await places.SaveAsync(PlaceKind.Home, HomePoint);
        await places.SaveAsync(PlaceKind.Work, WorkPoint);
        Answer(TripPath(HomePoint, WorkPoint), Trip(pick: 3, reference: 3, detourKm: 0.4, detourMin: 1, saves: "0"));

        var view = await _feeds.TripAsync(new WidgetPlaceRef(PlaceKind.Home), new WidgetPlaceRef(PlaceKind.Work), TestContext.Current.CancellationToken);

        Assert.Equal(WidgetProblem.None, view.Problem);
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
        Assert.Equal("Touchez ↻ pour trouver où vous êtes.", feeds.MessageFor(WidgetProblem.NoLocationYet, "Régulier"));
        Assert.Equal("Vous y êtes.", feeds.MessageFor(WidgetProblem.AtDestination, "Régulier"));
        Assert.Equal("Activez la localisation, puis touchez ↻.", feeds.MessageFor(WidgetProblem.LocationOff, "Régulier"));
    }

    [Fact]
    public async Task French_widgets_give_the_fix_s_time_or_day_in_french()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");
        var feeds = FeedsWith(WidgetLocationAccess.WhileInUse);
        var earlier = _time.GetUtcNow().AddHours(-2);
        var yesterday = _time.GetUtcNow().AddDays(-1);

        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));
        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 20, earlier));
        var today = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);
        Answer(BestPath(HomePoint), Best(pick: 1, reference: 1, "0"));
        await LastLocation.WriteAsync(_preferences, new LastFix(HomePoint.Latitude, HomePoint.Longitude, 2500, yesterday));
        var before = await feeds.BestNearAsync(WidgetPlaceRef.You, TestContext.Current.CancellationToken);

        Assert.EndsWith($" · Position à {TimeOfDay(earlier)}", today.Updated);
        Assert.EndsWith($" · Position du {Formatting.MonthDay(yesterday, _time.LocalTimeZone, CultureInfo.CurrentCulture)} · position approximative", before.Updated);
    }

    [Fact]
    public async Task Where_you_are_is_kept_as_an_empty_place_as_it_always_was()
    {
        await _preferences.SetAsync(WidgetConfigs.KeyFor(5), """{"origin":{"kind":null,"placeId":null},"from":{},"to":null}""");

        var config = await WidgetConfigs.ReadAsync(_preferences, 5);

        Assert.True(config.Origin!.IsYou);
        Assert.True(config.From!.IsYou);
        Assert.Null(config.To);
    }

    private void Answer(string pathAndQuery, HttpResponseMessage response) => _answers[pathAndQuery] = response;

    private WidgetFeeds FeedsWith(WidgetLocationAccess access, bool locationOn = true) => new(
        _preferences,
        new EssenceApiClient(new HttpClient(_http) { BaseAddress = new Uri("http://api.test/") }),
        new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)),
        _time,
        access,
        locationOn);

    /// <summary>Plain spaces: before "a.m.", ICU writes a narrow no-break space or a plain one, depending on its version.</summary>
    private static string Plain(string text) =>
        string.Concat(text.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c));

    private string TimeOfDay(DateTimeOffset at) => Formatting.TimeOfDay(at, _time.LocalTimeZone, CultureInfo.CurrentCulture);

    private string Updated() => $"Updated {TimeOfDay(_time.GetUtcNow())}";

    private static string BestPath(GeoPoint at) => string.Create(
        CultureInfo.InvariantCulture,
        $"/stations/best?lat={at.Latitude:0.######}&lon={at.Longitude:0.######}&fuel=R%C3%A9gulier&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10");

    private static string TripPath(GeoPoint from, GeoPoint to) => string.Create(
        CultureInfo.InvariantCulture,
        $"/trips/best?fromLat={from.Latitude:0.######}&fromLon={from.Longitude:0.######}&toLat={to.Latitude:0.######}&toLon={to.Longitude:0.######}&fuel=R%C3%A9gulier&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10&spareKm=30");

    /// <param name="more">More of the pick's fields, as JSON after a comma: its hours and ETA.</param>
    private static HttpResponseMessage Best(long pick, long reference, string saves, string more = "") => TestHttp.Json($$"""
        {"pick":{"id":{{pick}},"name":null,"brand":"Shell","address":"{{pick}} rue Principale","latitude":45.5,"longitude":-73.56,
          "priceCents":166.9,"distanceKm":{{pick}},"durationMin":{{2 * pick}},"costDollars":66.99,"savesDollars":{{saves}},
          "membersOnly":false,"overDetourLimit":false,"savingUncertain":false{{more}}},
         "referenceId":{{reference}},"closestId":1,"cheapestId":{{pick}},"stations":[],"radiusKm":10,"byRoad":true}
        """);

    /// <param name="hours">The station's <c>hours</c> as JSON; left out (an older API) when null.</param>
    private static HttpResponseMessage Station(long id, string? cents, string? hours = null) => TestHttp.Json($$"""
        {"id":{{id}},"name":null,"brand":"Shell","status":"En opération","address":"{{id}} rue Principale","postalCode":"H2X3J4",
         "region":"Montréal","latitude":45.5,"longitude":-73.56,"isActive":true,"firstSeenAt":"2026-10-01T00:00:00Z",
         "lastSeenAt":"2026-11-08T00:00:00Z","updatedAt":"2026-11-08T00:00:00Z",
         "prices":[{"fuel":"Régulier","priceCents":{{cents ?? "null"}},"isAvailable":{{(cents is null ? "false" : "true")}},"since":"2026-11-08T10:00:00Z"},
                   {"fuel":"Diesel","priceCents":150.0,"isAvailable":true,"since":"2026-11-08T10:00:00Z"}]{{(hours is null ? "" : $",\"hours\":{hours}")}}}
        """);

    private static HttpResponseMessage Trip(long pick, long reference, double detourKm, double detourMin, string saves) =>
        TripJson(pick, reference, detourKm.ToString(CultureInfo.InvariantCulture), detourMin.ToString(CultureInfo.InvariantCulture), saves);

    /// <param name="more">More of the pick's fields, as JSON after a comma: its hours and time from the start.</param>
    private static HttpResponseMessage TripJson(long pick, long reference, string detourKm, string detourMin, string saves, string more = "") =>
        TestHttp.Json($$"""
        {"path":[[-73.5673,45.5017],[-73.5662,45.4995]],"distanceKm":12.3,"durationMin":18,"halfWidthKm":2,"longTrip":false,
         "stopBeforeKm":null,"lowFuel":false,
         "stations":[{"id":{{pick}},"name":null,"brand":"Petro-Canada","address":"1 boul. Saint-Joseph","latitude":45.5,"longitude":-73.56,
           "priceCents":159.9,"kmAlong":5,"distanceKm":5,"detourKm":{{detourKm}},"detourMin":{{detourMin}},"costDollars":64.50,
           "savesDollars":{{saves}},"membersOnly":false,"reachable":true,"overDetourLimit":false{{more}}}],
         "pickId":{{pick}},"referenceId":{{reference}},"referenceOnTheWay":true,"biggestSavingId":null}
        """);
}
