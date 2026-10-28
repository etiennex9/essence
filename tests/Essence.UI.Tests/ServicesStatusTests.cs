using System.Net;
using System.Text;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class ServicesStatusTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Queue<string> _answers = new();
    private int _asked;

    private ServicesStatus NewStatus() =>
        new(new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") }), _time);

    private static string Services(string routing, string geocoding = Serving) => $$"""{"routing":{{routing}},"geocoding":{{geocoding}}}""";

    private const string Serving = """{"state":"serving","ready":true,"stage":null,"percent":null,"dataDate":"2026-10-27"}""";

    private static string Building(int percent) =>
        $$"""{"state":"building","ready":false,"stage":"extracting","percent":{{percent}},"dataDate":null}""";

    [Fact]
    public async Task Nothing_is_asked_until_a_pane_watches()
    {
        var status = NewStatus();
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();

        Assert.Equal(0, _asked);
        Assert.Null(status.Routing);
    }

    [Fact]
    public async Task While_routing_is_prepared_it_s_asked_every_10_seconds_until_it_s_ready()
    {
        _answers.Enqueue(Services(Building(45)));
        _answers.Enqueue(Services(Building(80)));
        _answers.Enqueue(Services(Serving));
        var status = NewStatus();
        var ready = 0;
        status.RoutingReady += () => ready++;

        using var watching = status.Watch();
        await Until(() => _asked == 1);
        Assert.True(status.RoutingPreparing);
        Assert.Equal(45, status.Routing!.Percent);

        _time.Advance(ServicesStatus.PollEvery);
        await Until(() => _asked == 2);
        Assert.Equal(80, status.Routing!.Percent);

        _time.Advance(ServicesStatus.PollEvery);
        await Until(() => _asked == 3);
        Assert.False(status.RoutingPreparing);
        Assert.Equal(1, ready);

        // Both ready: no more asking.
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(3, _asked);
    }

    [Fact]
    public async Task Closing_the_pane_stops_asking()
    {
        _answers.Enqueue(Services(Building(10)));
        var status = NewStatus();

        var watching = status.Watch();
        await Until(() => _asked == 1);
        watching.Dispose();
        watching.Dispose();

        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task Two_panes_ask_once()
    {
        _answers.Enqueue(Services(Building(10)));
        var status = NewStatus();

        using var first = status.Watch();
        using var second = status.Watch();
        await Until(() => _asked == 1);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task A_503_asks_again_at_once()
    {
        _answers.Enqueue(Services(Serving));
        _answers.Enqueue(Services(Serving, Building(30)));
        var status = NewStatus();
        using var watching = status.Watch();
        await Until(() => _asked == 1);

        await status.RefreshAsync();

        Assert.Equal(2, _asked);
        Assert.True(status.GeocodingPreparing);
        Assert.False(status.RoutingPreparing);
    }

    [Fact]
    public async Task A_service_that_doesn_t_say_isn_t_called_preparing()
    {
        _answers.Enqueue(Services("""{"state":"unknown","ready":true,"stage":null,"percent":null,"dataDate":null}"""));
        var status = NewStatus();

        using var watching = status.Watch();
        await Until(() => _asked == 1);

        Assert.False(status.RoutingPreparing);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class StubHandler(ServicesStatusTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/services", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref test._asked);
            var body = test._answers.Count > 0 ? test._answers.Dequeue() : Services(Serving);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
