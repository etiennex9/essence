using System.Net;
using System.Text;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class BestNearYouTests
{
    private const string BestJson = """
        {"pick":{"id":1,"name":null,"brand":"Ultramar","address":"1 rue Principale","latitude":45.5,"longitude":-73.56,
          "priceCents":166.9,"distanceKm":0.8,"durationMin":null,"costDollars":66.99,"savesDollars":0,"membersOnly":false,
          "overDetourLimit":false,"savingUncertain":false},
         "referenceId":1,"closestId":1,"cheapestId":1,"stations":[],"radiusKm":10,"byRoad":false}
        """;

    /// <summary>Picked 2, closest 1, cheapest 3; 4 saves under $0.25, so the pane doesn't list it.</summary>
    private static readonly string RankedJson = $$"""
        {"pick":{{Ranked(2, "1.20")}},"referenceId":1,"closestId":1,"cheapestId":3,
         "stations":[{{Ranked(1, "0")}},{{Ranked(2, "1.20")}},{{Ranked(3, "0.30")}},{{Ranked(4, "0.10")}}],"radiusKm":10,"byRoad":true}
        """;

    private static readonly BestOrigin Montreal = new(new GeoPoint(45.5017, -73.5673), AccuracyMeters: 20);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryPreferences _preferences = new();
    private readonly List<Uri> _requests = [];
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _json = BestJson;
    private DateTimeOffset _importedAt;
    private readonly CarProfiles _cars;
    private readonly BestNearYou _best;

    public BestNearYouTests()
    {
        _importedAt = _time.GetUtcNow().AddMinutes(-1);
        var http = new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") };
        _cars = new CarProfiles(_preferences);
        _best = new BestNearYou(new EssenceApiClient(http), _cars, _time);
    }

    [Fact]
    public async Task Nothing_is_asked_until_the_location_is_known()
    {
        await _best.SetFuelAsync("Diesel");

        Assert.Empty(_requests);
        Assert.Null(_best.Result);
    }

    [Fact]
    public async Task The_location_asks_for_the_map_fuel_and_the_car()
    {
        await _best.SetFuelAsync("Super");

        await _best.SetOriginAsync(Montreal);

        var query = Assert.Single(_requests).Query;
        Assert.Contains("lat=45.5017&lon=-73.5673&fuel=Super&fillL=40&per100Km=8.5&costco=false&maxDetourMin=10", query);
        Assert.Equal(1, _best.Result!.Pick!.Id);
        Assert.Equal("Super", _best.ResultQuery!.Fuel);
        Assert.Equal(_importedAt, _best.DataAsOf);
        Assert.False(_best.Loading);
    }

    [Fact]
    public async Task Another_car_or_its_settings_rank_again()
    {
        await _best.SetOriginAsync(Montreal);

        await _cars.SaveAsync(_cars.Active with { CostcoMember = true, MaxDetourMinutes = 15 });

        Assert.Equal(2, _requests.Count);
        Assert.Contains("costco=true&maxDetourMin=15", _requests[1].Query);
    }

    [Fact]
    public async Task It_refreshes_when_the_next_prices_should_be_in()
    {
        await _best.SetOriginAsync(Montreal);

        // Imported a minute ago: the next import should be in 4 min 20 s from now.
        _time.Advance(TimeSpan.FromSeconds(259));
        Assert.Single(_requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task A_hidden_page_waits_and_refreshes_once_shown()
    {
        await _best.SetOriginAsync(Montreal);
        _best.SetVisible(false);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(_requests);

        _best.SetVisible(true);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_what_is_shown()
    {
        await _best.SetOriginAsync(Montreal);
        _status = HttpStatusCode.InternalServerError;

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(2, _requests.Count);
        Assert.NotNull(_best.Result);
        Assert.Equal(ApiError.None, _best.Error);
    }

    [Fact]
    public async Task A_failure_for_new_inputs_clears_the_result_until_retried()
    {
        await _best.SetOriginAsync(Montreal);
        _status = HttpStatusCode.ServiceUnavailable;

        await _best.SetFuelAsync("Diesel");

        Assert.Null(_best.Result);
        Assert.Equal(ApiError.Unavailable, _best.Error);

        _status = HttpStatusCode.OK;
        await _best.RetryAsync();
        Assert.Equal("Diesel", _best.ResultQuery!.Fuel);
        Assert.Equal(ApiError.None, _best.Error);
    }

    [Fact]
    public async Task Forgetting_the_location_forgets_the_result()
    {
        await _best.SetOriginAsync(Montreal);

        await _best.SetOriginAsync(null);

        Assert.Null(_best.Result);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(_requests);
    }

    [Fact]
    public async Task A_refresh_keeps_the_station_selected()
    {
        _json = RankedJson;
        await _best.SetOriginAsync(Montreal);

        _best.Select(3);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(2, _requests.Count);
        Assert.Equal(3, _best.SelectedId);
        Assert.Equal(3, _best.Shown!.Id);
    }

    [Fact]
    public async Task Another_car_shows_the_pick_again()
    {
        _json = RankedJson;
        await _best.SetOriginAsync(Montreal);
        _best.Select(3);

        await _cars.SaveAsync(_cars.Active with { FillLitres = 50 });

        Assert.Null(_best.SelectedId);
        Assert.Equal(2, _best.Shown!.Id);
    }

    [Fact]
    public async Task Only_a_station_the_pane_lists_can_be_selected()
    {
        _json = RankedJson;
        await _best.SetOriginAsync(Montreal);
        var changes = 0;
        _best.Changed += () => changes++;

        _best.Select(4);
        _best.Select(99);
        _best.Select(2);

        Assert.Null(_best.SelectedId);
        Assert.Equal(2, _best.Shown!.Id);
        Assert.Equal(0, changes);

        _best.Select(1);
        Assert.Equal(1, _best.Shown!.Id);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_pick_is_shown_even_when_it_isnt_among_the_stations()
    {
        await _best.SetOriginAsync(Montreal);

        Assert.Equal(1, _best.Shown!.Id);
    }

    [Theory]
    [InlineData(OriginKind.Device, 1500.0, true)]
    [InlineData(OriginKind.Device, 1000.0, false)]
    [InlineData(OriginKind.MapCenter, null, false)]
    [InlineData(OriginKind.Pin, 1500.0, false)]
    public void Only_an_inaccurate_device_location_is_approximate(OriginKind kind, double? accuracy, bool approximate) =>
        Assert.Equal(approximate, new BestOrigin(new GeoPoint(45.5, -73.6), kind, accuracy).IsApproximate);

    [Fact]
    public async Task A_pin_ranks_from_the_pin_until_the_location_comes_back()
    {
        await _best.SetOriginAsync(new BestOrigin(new GeoPoint(45.6418, -73.8410), OriginKind.Pin, Label: "65 Rue Péladeau"));

        Assert.Contains("lat=45.6418&lon=-73.841&", Assert.Single(_requests).Query);
        Assert.True(_best.Origin!.IsChosen);

        await _best.SetOriginAsync(Montreal);

        Assert.Equal(2, _requests.Count);
        Assert.Contains("lat=45.5017&lon=-73.5673&", _requests[1].Query);
        Assert.False(_best.Origin!.IsChosen);
    }

    private static string Ranked(long id, string savesDollars) => $$"""
        {"id":{{id}},"name":null,"brand":"Shell","address":"{{id}} rue Principale","latitude":45.5,"longitude":-73.56,"priceCents":166.9,
         "distanceKm":{{id}},"durationMin":{{2 * id}},"costDollars":66.99,"savesDollars":{{savesDollars}},"membersOnly":false,
         "overDetourLimit":false,"savingUncertain":false}
        """;

    private sealed class StubHandler(BestNearYouTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(test._status);
            if (test._status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(test._json, Encoding.UTF8, "application/json");
                response.Content.Headers.LastModified = test._importedAt;
            }

            return Task.FromResult(response);
        }
    }
}
