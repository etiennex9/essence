using System.Net;
using System.Text;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class TripStateTests
{
    private const string PlanJson = """
        {"path":[[-73.5673,45.5017],[-71.208,46.8139]],"distanceKm":250.3,"durationMin":155.2,"halfWidthKm":5,"longTrip":true,
         "stopBeforeKm":150,"lowFuel":false,
         "stations":[{"id":3,"name":null,"brand":"Petro-Canada","address":"1 boul. Saint-Joseph, Drummondville","latitude":45.88,
           "longitude":-72.48,"priceCents":159.9,"kmAlong":105,"distanceKm":107,"detourKm":4,"detourMin":5,"costDollars":64.50,
           "savesDollars":1.26,"membersOnly":false,"reachable":true,"overDetourLimit":false}],
         "pickId":3,"referenceId":3,"referenceOnTheWay":false,"biggestSavingId":null}
        """;

    private static readonly GeoPoint Montreal = new(45.5017, -73.5673);
    private static readonly TripDestination Quebec = new(46.8139, -71.208);
    private static readonly TripDestination Work = new(45.4995, -73.5662, PlaceKind.Work);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryPreferences _preferences = new();
    private readonly MemorySession _session = new();
    private readonly List<Uri> _requests = [];
    private HttpStatusCode _status = HttpStatusCode.OK;
    private readonly CarProfiles _cars;

    public TripStateTests() => _cars = new CarProfiles(_preferences);

    private TripState NewTrip() =>
        new(new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") }), _cars, _session, _time);

    [Fact]
    public async Task A_trip_is_planned_from_the_start_for_the_car()
    {
        var trip = NewTrip();
        await trip.SetFuelAsync("Super");

        await trip.StartAsync(Montreal, Quebec);

        var query = Assert.Single(_requests).Query;
        Assert.Equal(
            "?fromLat=45.5017&fromLon=-73.5673&toLat=46.8139&toLon=-71.208&fuel=Super&fillL=40&per100Km=8.5&costco=false"
            + "&maxDetourMin=10&spareKm=30",
            query);
        Assert.True(trip.IsOpen);
        Assert.Equal(3, trip.Plan!.PickId);
        Assert.Equal(_time.GetUtcNow(), trip.PlannedAt);
        Assert.False(trip.Loading);
    }

    [Fact]
    public async Task The_cars_range_is_asked_for_and_typing_it_plans_again_from_the_same_start()
    {
        var trip = NewTrip();
        await trip.StartAsync(Montreal, Quebec);

        await _cars.SetRangeAsync(180, _time.GetUtcNow());

        Assert.Equal(2, _requests.Count);
        Assert.Contains("&rangeKm=180&spareKm=30", _requests[1].Query);
        Assert.Contains("fromLat=45.5017&fromLon=-73.5673", _requests[1].Query);
    }

    [Fact]
    public async Task The_same_inputs_arent_planned_twice()
    {
        var trip = NewTrip();
        await trip.StartAsync(Montreal, Quebec);

        await trip.SetFuelAsync(Fuels.Default);
        await _cars.SetFuelAsync(Fuels.Default);

        Assert.Single(_requests);
    }

    [Fact]
    public async Task Refreshing_plans_again_from_where_the_user_is_now()
    {
        var trip = NewTrip();
        await trip.StartAsync(Montreal, Quebec);

        await trip.RefreshAsync(new GeoPoint(45.8833, -72.4842));

        Assert.Equal(2, _requests.Count);
        Assert.StartsWith("?fromLat=45.8833&fromLon=-72.4842", _requests[1].Query);
        Assert.Equal(new GeoPoint(45.8833, -72.4842), trip.Origin);
    }

    [Fact]
    public async Task A_trip_survives_the_tab_being_discarded_without_asking_again()
    {
        var trip = NewTrip();
        await trip.StartAsync(Montreal, Work);

        var restored = NewTrip();
        await restored.LoadAsync();
        await restored.SetFuelAsync(Fuels.Default);

        Assert.Single(_requests);
        Assert.True(restored.IsOpen);
        Assert.Equal(Work, restored.Destination);
        Assert.Equal(Montreal, restored.Origin);
        Assert.Equal(3, restored.Plan!.PickId);
        Assert.Equal(trip.PlannedAt, restored.PlannedAt);
    }

    [Fact]
    public async Task A_restored_trip_for_another_fuel_is_planned_again()
    {
        var trip = NewTrip();
        await trip.StartAsync(Montreal, Quebec);

        var restored = NewTrip();
        await restored.LoadAsync();
        await restored.SetFuelAsync("Diesel");

        Assert.Equal(2, _requests.Count);
        Assert.Contains("fuel=Diesel", _requests[1].Query);
    }

    [Fact]
    public async Task Ending_the_trip_forgets_it_in_the_tab_too()
    {
        var trip = NewTrip();
        await trip.StartAsync(Montreal, Quebec);

        await trip.EndAsync();

        Assert.False(trip.IsOpen);
        Assert.Null(trip.Plan);
        var restored = NewTrip();
        await restored.LoadAsync();
        Assert.False(restored.IsOpen);
    }

    [Fact]
    public async Task Without_a_trip_car_changes_plan_nothing()
    {
        NewTrip();

        await _cars.SetRangeAsync(180, _time.GetUtcNow());

        Assert.Empty(_requests);
    }

    [Fact]
    public async Task A_503_says_trips_are_unavailable_for_a_while()
    {
        var trip = NewTrip();
        _status = HttpStatusCode.ServiceUnavailable;

        await trip.StartAsync(Montreal, Quebec);

        Assert.Equal(ApiError.Unavailable, trip.Error);
        Assert.Null(trip.Plan);
        Assert.True(trip.Unavailable);

        _time.Advance(TripState.UnavailableFor);
        Assert.False(trip.Unavailable);

        _status = HttpStatusCode.OK;
        await trip.RetryAsync();
        Assert.Equal(ApiError.None, trip.Error);
        Assert.NotNull(trip.Plan);
    }

    [Fact]
    public async Task No_road_near_the_destination_is_told_apart()
    {
        var trip = NewTrip();
        _status = HttpStatusCode.UnprocessableEntity;

        await trip.StartAsync(Montreal, new TripDestination(45.42, -75.69));

        Assert.Equal(ApiError.NoRoute, trip.Error);
        Assert.False(trip.Unavailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"destination":null,"origin":{"latitude":45,"longitude":-73}}""")]
    public async Task An_unreadable_trip_is_none(string json)
    {
        await _session.SetAsync(TripState.StorageKey, json);
        var trip = NewTrip();

        await trip.LoadAsync();

        Assert.False(trip.IsOpen);
    }

    private sealed class StubHandler(TripStateTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(test._status);
            if (test._status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(PlanJson, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
