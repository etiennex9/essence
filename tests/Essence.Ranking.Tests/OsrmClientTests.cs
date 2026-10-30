using System.Net;
using System.Text;

namespace Essence.Ranking.Tests;

public class OsrmClientTests
{
    private static readonly LatLon Montreal = new(45.5017, -73.5673);
    private static readonly LatLon Quebec = new(46.8139, -71.208);

    private readonly List<Uri> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Json(HttpStatusCode.OK, """{"code":"Ok"}""");

    private OsrmClient Client => new(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://routing.test:5000/") });

    [Fact]
    public async Task A_route_asks_for_its_full_line_snapped_to_any_road_and_reads_it()
    {
        _respond = _ => Json(HttpStatusCode.OK, """
            {"code":"Ok","routes":[{"geometry":"abc","distance":250312.4,"duration":9301.2,"legs":[],"weight":9400}],
             "waypoints":[{"hint":"AAAA","distance":3.2,"name":"","location":[-73.567301,45.501702]},
                          {"distance":812.5,"name":"","location":[-71.2,46.81]}]}
            """);

        var route = await Client.RouteAsync(Montreal, Quebec, TestContext.Current.CancellationToken);

        Assert.Equal("abc", route.Geometry);
        Assert.Equal(250312.4, route.DistanceMeters);
        Assert.Equal(9301.2, route.DurationSeconds);
        Assert.Equal([new OsrmWaypoint(new(45.501702, -73.567301), 3.2), new OsrmWaypoint(new(46.81, -71.2), 812.5)], route.Waypoints);
        Assert.Equal(
            "/route/v1/driving/-73.5673,45.5017;-71.208,46.8139?overview=full&geometries=polyline6&steps=false&generate_hints=false"
            + "&snapping=any",
            Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task A_table_names_its_sources_and_destinations_and_keeps_missing_roads()
    {
        _respond = _ => Json(HttpStatusCode.OK, """
            {"code":"Ok","distances":[[0,1200.5,null]],"durations":[[0,95.1,null]],
             "sources":[{"distance":3.2,"name":"","location":[-73.5673,45.5017]}],
             "destinations":[{"distance":3.2,"location":[-73.5673,45.5017]},{"distance":12,"location":[-71.208,46.8139]},
                             {"distance":40.5,"location":[-73.6,45.55]}]}
            """);

        var table = await Client.TableAsync(
            [Montreal, Quebec, new LatLon(45.55, -73.6)], [0], [0, 1, 2], anyRoad: false, TestContext.Current.CancellationToken);

        Assert.Equal([0, 1200.5, null], table.DistancesMeters[0]);
        Assert.Equal([0, 95.1, null], table.DurationsSeconds[0]);
        Assert.Equal(new OsrmWaypoint(Montreal, 3.2), Assert.Single(table.Sources));
        Assert.Equal([3.2, 12, 40.5], table.Destinations.Select(d => d.SnapMeters));
        Assert.Equal(
            "/table/v1/driving/-73.5673,45.5017;-71.208,46.8139;-73.6,45.55?annotations=distance,duration&generate_hints=false"
            + "&sources=0&destinations=0;1;2",
            Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task A_table_on_any_road_says_so()
    {
        _respond = _ => Json(HttpStatusCode.OK, """
            {"code":"Ok","distances":[[5]],"durations":[[1]],"sources":[{"distance":1,"location":[0,0]}],
             "destinations":[{"distance":1,"location":[0,0]}]}
            """);

        await Client.TableAsync([Montreal, Quebec], [0], [1], anyRoad: true, TestContext.Current.CancellationToken);

        Assert.EndsWith("&sources=0&destinations=1&snapping=any", Assert.Single(_requests).Query);
    }

    [Fact]
    public async Task Snapping_puts_each_point_on_the_profiles_roads_in_one_table()
    {
        _respond = _ => Json(HttpStatusCode.OK, """
            {"code":"Ok","distances":[[0,5]],"durations":[[0,1]],"sources":[{"distance":24.4,"location":[-73.837348,45.458469]}],
             "destinations":[{"distance":24.4,"location":[-73.837348,45.458469]},{"distance":301,"location":[-73.6,45.55]}]}
            """);

        var snapped = await Client.SnapAsync([new(45.458287, -73.837174), new(45.551, -73.6)], TestContext.Current.CancellationToken);

        Assert.Equal([new OsrmWaypoint(new(45.458469, -73.837348), 24.4), new OsrmWaypoint(new(45.55, -73.6), 301)], snapped);
        Assert.Equal("?annotations=distance,duration&generate_hints=false&sources=0&destinations=0;1", Assert.Single(_requests).Query);
    }

    [Fact]
    public async Task Snapping_nothing_asks_nothing()
    {
        Assert.Empty(await Client.SnapAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Nearest_reads_where_the_point_snapped()
    {
        _respond = _ => Json(HttpStatusCode.OK, """
            {"code":"Ok","waypoints":[{"nodes":[1,2],"distance":1500.25,"name":"A-40","location":[-73.56,45.5]}]}
            """);

        var waypoint = await Client.NearestAsync(Montreal, TestContext.Current.CancellationToken);

        Assert.Equal(new OsrmWaypoint(new(45.5, -73.56), 1500.25), waypoint);
        Assert.Equal("/nearest/v1/driving/-73.5673,45.5017?number=1&generate_hints=false", Assert.Single(_requests).PathAndQuery);
    }

    [Theory]
    [InlineData("NoRoute", OsrmFailure.NoRoute)]
    [InlineData("NoSegment", OsrmFailure.NoRoute)]
    [InlineData("TooBig", OsrmFailure.Bug)]
    [InlineData("InvalidQuery", OsrmFailure.Bug)]
    [InlineData("InvalidOptions", OsrmFailure.Bug)]
    public async Task Every_error_is_a_400_told_apart_by_its_code(string code, OsrmFailure failure)
    {
        _respond = _ => Json(HttpStatusCode.BadRequest, $$"""{"code":"{{code}}","message":"Something about it."}""");

        var error = await Assert.ThrowsAsync<OsrmException>(() => Client.RouteAsync(Montreal, Quebec, TestContext.Current.CancellationToken));

        Assert.Equal(failure, error.Failure);
        Assert.Equal(code, error.Code);
        Assert.Contains("Something about it.", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_server_error_is_unavailable(HttpStatusCode status)
    {
        _respond = _ => new HttpResponseMessage(status);

        var error = await Assert.ThrowsAsync<OsrmException>(() => Client.NearestAsync(Montreal, TestContext.Current.CancellationToken));

        Assert.Equal(OsrmFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable()
    {
        _respond = _ => throw new HttpRequestException("Connection refused (routing:5000)");

        var error = await Assert.ThrowsAsync<OsrmException>(() => Client.NearestAsync(Montreal, TestContext.Current.CancellationToken));

        Assert.Equal(OsrmFailure.Unavailable, error.Failure);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task A_timeout_is_unavailable()
    {
        _respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var error = await Assert.ThrowsAsync<OsrmException>(() => Client.NearestAsync(Montreal, TestContext.Current.CancellationToken));

        Assert.Equal(OsrmFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task Cancelling_the_call_cancels_it()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.NearestAsync(Montreal, cancelled.Token));
    }

    [Theory]
    [InlineData("""<html>Bad Gateway</html>""")]
    [InlineData("""{"code":"Ok"}""")]
    [InlineData("""{"code":"Ok","routes":[{"geometry":"abc"}],"waypoints":[]}""")]
    [InlineData("""{"code":"Ok","routes":[{"geometry":"abc","distance":1,"duration":1}],"waypoints":[{"distance":1},{"distance":1}]}""")]
    public async Task An_answer_that_isnt_what_osrm_sends_is_a_bug(string body)
    {
        _respond = _ => Json(HttpStatusCode.OK, body);

        var error = await Assert.ThrowsAsync<OsrmException>(() => Client.RouteAsync(Montreal, Quebec, TestContext.Current.CancellationToken));

        Assert.Equal(OsrmFailure.Bug, error.Failure);
    }

    [Fact]
    public async Task A_table_of_the_wrong_size_is_a_bug()
    {
        _respond = _ => Json(HttpStatusCode.OK, """
            {"code":"Ok","distances":[[5]],"durations":[[1]],"sources":[{"distance":1,"location":[0,0]}],
             "destinations":[{"distance":1,"location":[0,0]}]}
            """);

        var error = await Assert.ThrowsAsync<OsrmException>(
            () => Client.TableAsync([Montreal, Quebec], [0], [0, 1], anyRoad: false, TestContext.Current.CancellationToken));

        Assert.Equal(OsrmFailure.Bug, error.Failure);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(OsrmClientTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            test._requests.Add(request.RequestUri!);
            return Task.FromResult(test._respond(request));
        }
    }
}
