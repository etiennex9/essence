using System.Globalization;
using System.Net;
using Essence.Contracts;
using Essence.Hours;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

/// <summary>
/// The hours the user corrected on the device, wherever the app says a station's hours: they win over the server's, their
/// source is "Your hours", the arrival is checked with them on the device, and Best near you and Best on the way send their
/// stations as <c>ignoreHours</c>, asking again when one is saved. On Costco Laval's gas bar, Wednesday, November 11 2026.
/// </summary>
public sealed class CorrectedHoursTests : IDisposable
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";
    private const double Latitude = 45.5699;
    private const double Longitude = -73.7469;
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(-5));

    private static readonly StationHours Costco = new(CostcoRule, "costco", Trusted: true, Confirmed: true, null, "https://www.costco.ca/");

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly FakeTimeProvider _time = new(Wednesday.AddHours(21).AddMinutes(45));
    private readonly MemoryPreferences _preferences = new();
    private readonly StationHoursOverrides _overrides;
    private readonly StationHoursView _view;

    public CorrectedHoursTests()
    {
        Culture("en-CA");
        _overrides = new StationHoursOverrides(_preferences, _time);
        _view = new StationHoursView(
            new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)),
            _overrides);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Theory]
    [InlineData("en-CA", "Your hours", "Open until 11:00 p.m.")]
    [InlineData("fr-CA", "Vos heures", "Ouvert jusqu’à 23 h")]
    public async Task The_device_s_hours_win_over_the_server_s_and_say_whose_they_are(string culture, string source, string status)
    {
        Culture(culture);
        await _overrides.SaveAsync(1, OpeningHours.Parse("Mo-Su 06:00-23:00"), "Costco", null);

        var view = _view.Describe(_view.HoursOf(1, Costco), Latitude, Longitude, _time.GetUtcNow());

        Assert.Equal(HoursKind.Open, view.Kind);
        Assert.Equal((source, status), (view.Source, Plain(view.Status)));
        Assert.Null(view.SourceLink);
        Assert.Equal(HoursKind.Closed, _view.Describe(_view.HoursOf(2, Costco), Latitude, Longitude, _time.GetUtcNow()).Kind);
    }

    [Fact]
    public async Task Hours_added_where_the_server_has_none_show_too_and_the_map_draws_them()
    {
        await _overrides.SaveAsync(1, OpeningHours.Parse(CostcoRule), "Costco", null);

        Assert.Equal(HoursKind.Closed, _view.Describe(_view.HoursOf(1, null), Latitude, Longitude, _time.GetUtcNow()).Kind);
        Assert.True(StationHoursView.MarkerAt(_view.HoursOf(1, null), Latitude, Longitude, _time.GetUtcNow()).Closed);
        Assert.False(StationHoursView.MarkerAt(_view.HoursOf(2, null), Latitude, Longitude, _time.GetUtcNow()).Closed);
    }

    [Fact]
    public async Task The_arrival_is_checked_on_the_device_with_the_corrected_hours()
    {
        // The server says open around the clock, so it never leaves it out; the user says Costco's hours.
        var server = Costco with { Rule = "24/7" };
        await _overrides.SaveAsync(1, OpeningHours.Parse(CostcoRule), "Costco", null);

        var arrival = _view.Arrive(_view.HoursOf(1, server), Latitude, Longitude, Wednesday.AddHours(21).AddMinutes(15), etaMinutes: 20, "Costco");

        Assert.Equal(ArrivalKind.ClosesBefore, arrival.Kind);
        Assert.True(arrival.Warns);
        Assert.Equal("Closed by the time you get there (closes 9:30 p.m.).", Plain(arrival.Blocked));
        Assert.Equal(ArrivalKind.Open, _view.Arrive(_view.HoursOf(2, server), Latitude, Longitude, _time.GetUtcNow(), 20, "Costco").Kind);
    }

    [Fact]
    public async Task A_station_left_out_that_the_user_says_is_open_isn_t_said_to_be()
    {
        var leftOut = new LeftOutStation(1, null, "Costco", "1 rue Principale", Latitude, Longitude, 150.9m, Costco, EtaMin: 10);
        Assert.NotNull(_view.LeftOut(leftOut, "Costco", _time.GetUtcNow()));

        await _overrides.SaveAsync(1, OpeningHours.AlwaysOpen, "Costco", null);

        Assert.Null(_view.LeftOut(leftOut, "Costco", _time.GetUtcNow()));
    }

    [Fact]
    public void Without_corrections_the_server_s_hours_are_the_ones()
    {
        var view = new StationHoursView(new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)));

        Assert.Same(Costco, view.HoursOf(1, Costco));
    }

    [Fact]
    public async Task Best_near_you_sends_the_corrected_stations_and_asks_again_when_one_is_saved()
    {
        await _overrides.SaveAsync(42, OpeningHours.AlwaysOpen, null, null);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _overrides.SaveAsync(7, OpeningHours.AlwaysOpen, null, null);
        var http = new TestHttp(_ => TestHttp.Json(BestJson));
        var best = new BestNearYou(Api(http), new CarProfiles(_preferences), _time, _overrides);

        await best.SetOriginAsync(new BestOrigin(new GeoPoint(45.5017, -73.5673), AccuracyMeters: 20));
        Assert.EndsWith("&maxDetourMin=10&ignoreHours=7,42", Assert.Single(http.Requests).RequestUri!.Query);

        await _overrides.SaveAsync(3, OpeningHours.Parse(CostcoRule), null, null);
        await Until(() => http.Requests.Count == 2);

        Assert.EndsWith("&ignoreHours=3,7,42", http.Requests[1].RequestUri!.Query);
        Assert.NotNull(best.Result);
        Assert.False(best.Loading);

        await _overrides.ResetAsync(3);
        await _overrides.ResetAsync(7);
        await _overrides.ResetAsync(42);
        await Until(() => http.Requests.Count == 5);
        Assert.DoesNotContain("ignoreHours", http.Requests[4].RequestUri!.Query);
    }

    [Fact]
    public async Task Best_on_the_way_sends_them_too_and_plans_again_when_one_is_saved()
    {
        await _overrides.SaveAsync(42, OpeningHours.AlwaysOpen, null, null);
        var http = new TestHttp(_ => TestHttp.Json(PlanJson));
        var trip = new TripState(Api(http), new CarProfiles(_preferences), new MemorySession(), _time, _overrides);

        await trip.StartAsync(new GeoPoint(45.5017, -73.5673), new TripDestination(46.8139, -71.208));
        Assert.EndsWith("&spareKm=30&ignoreHours=42", Assert.Single(http.Requests).RequestUri!.Query);

        await _overrides.SaveAsync(3, OpeningHours.Parse(CostcoRule), null, null);
        await Until(() => http.Requests.Count == 2 && !trip.Loading);

        Assert.EndsWith("&spareKm=30&ignoreHours=3,42", http.Requests[1].RequestUri!.Query);
        Assert.Contains("fromLat=45.5017&fromLon=-73.5673", http.Requests[1].RequestUri!.Query);
        Assert.Equal(3, trip.Plan!.PickId);
    }

    [Fact]
    public async Task A_trip_kept_in_the_tab_isn_t_planned_again_for_the_corrections_coming_in()
    {
        await _overrides.SaveAsync(42, OpeningHours.AlwaysOpen, null, null);
        var session = new MemorySession();
        var http = new TestHttp(_ => TestHttp.Json(PlanJson));
        await new TripState(Api(http), new CarProfiles(_preferences), session, _time, _overrides)
            .StartAsync(new GeoPoint(45.5017, -73.5673), new TripDestination(46.8139, -71.208));

        // The tab restored: a new page, whose corrections are read again.
        var overrides = new StationHoursOverrides(_preferences, _time);
        var restored = new TripState(Api(http), new CarProfiles(_preferences), session, _time, overrides);
        await restored.LoadAsync();
        await restored.SetFuelAsync(Fuels.Default);

        Assert.Single(http.Requests);
        Assert.NotNull(restored.Plan);
    }

    private const string BestJson = """
        {"pick":{"id":1,"name":null,"brand":"Costco","address":"1 rue Principale","latitude":45.5699,"longitude":-73.7469,
          "priceCents":150.9,"distanceKm":0.8,"durationMin":null,"costDollars":60.36,"savesDollars":0,"membersOnly":true,
          "overDetourLimit":false,"savingUncertain":false},
         "referenceId":1,"closestId":1,"cheapestId":1,"stations":[],"radiusKm":10,"byRoad":false}
        """;

    private const string PlanJson = """
        {"path":[[-73.5673,45.5017],[-71.208,46.8139]],"distanceKm":250.3,"durationMin":155.2,"halfWidthKm":5,"longTrip":true,
         "stopBeforeKm":150,"lowFuel":false,
         "stations":[{"id":3,"name":null,"brand":"Petro-Canada","address":"1 boul. Saint-Joseph, Drummondville","latitude":45.88,
           "longitude":-72.48,"priceCents":159.9,"kmAlong":105,"distanceKm":107,"detourKm":4,"detourMin":5,"costDollars":64.50,
           "savesDollars":1.26,"membersOnly":false,"reachable":true,"overDetourLimit":false}],
         "pickId":3,"referenceId":3,"referenceOnTheWay":false,"biggestSavingId":null}
        """;

    private static EssenceApiClient Api(TestHttp http) => new(new HttpClient(http) { BaseAddress = new Uri("http://api.test/") });

    /// <summary>Waits for what a correction set off in the background, a few seconds at most.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(done());
    }

    private static void Culture(string name) =>
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    /// <summary>Plain spaces: before "a.m.", ICU writes a narrow no-break space or a plain one, depending on its version.</summary>
    private static string? Plain(string? text) =>
        text is null ? null : string.Concat(text.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c));
}
