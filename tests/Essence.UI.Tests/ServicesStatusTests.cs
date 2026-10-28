using System.Globalization;
using System.Net;
using System.Text;
using Essence.Contracts;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class ServicesStatusTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Queue<string> _answers = new();
    private int _asked;
    private int _searched;
    private HttpStatusCode _searchStatus = HttpStatusCode.ServiceUnavailable;

    private ServicesStatus NewStatus() =>
        new(new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") }), _time);

    private static string Services(string routing, string geocoding = Serving) => $$"""{"routing":{{routing}},"geocoding":{{geocoding}}}""";

    private const string Serving = """{"state":"serving","ready":true,"stage":null,"percent":null,"dataDate":"2026-10-27"}""";

    private static string Building(int percent) =>
        $$"""{"state":"building","ready":false,"stage":"extracting","percent":{{percent}},"dataDate":null}""";

    private const string Unknown = """{"state":"unknown","ready":true,"stage":null,"percent":null,"dataDate":null}""";

    [Fact]
    public async Task Nothing_is_asked_until_the_map_watches()
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
    public async Task Geocoding_ready_is_raised_when_the_index_goes_from_prepared_to_ready_only()
    {
        _answers.Enqueue(Services(Serving));
        _answers.Enqueue(Services(Serving));
        _answers.Enqueue(Services(Serving, Building(90)));
        _answers.Enqueue(Services(Serving));
        var status = NewStatus();
        var ready = 0;
        status.GeocodingReady += () => ready++;
        using var watching = status.Watch();
        await Until(() => _asked == 1);

        await status.RefreshAsync();
        Assert.Equal(0, ready);
        await status.RefreshAsync();
        _time.Advance(ServicesStatus.PollEvery);
        await Until(() => _asked == 4);

        Assert.Equal(1, ready);
    }

    [Fact]
    public async Task An_API_without_services_isn_t_asked_every_10_seconds()
    {
        _answers.Enqueue("not json");
        var status = NewStatus();

        using var watching = status.Watch();
        await Until(() => _asked == 1);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task A_failed_answer_while_routing_is_prepared_keeps_asking()
    {
        _answers.Enqueue(Services(Building(45)));
        _answers.Enqueue("not json");
        _answers.Enqueue(Services(Building(60)));
        var status = NewStatus();

        using var watching = status.Watch();
        await Until(() => _asked == 1);
        _time.Advance(ServicesStatus.PollEvery);
        await Until(() => _asked == 2);
        _time.Advance(ServicesStatus.PollEvery);
        await Until(() => _asked == 3);

        Assert.Equal(60, status.Routing!.Percent);
    }

    [Fact]
    public async Task A_search_refused_while_the_index_reads_ready_asks_its_status_once_and_nothing_more()
    {
        // Photon down while its status can't be had: the API says unknown (ready). Answered quickly, so that a loop shows.
        _answers.Enqueue(Services(Serving, Unknown));
        var status = NewStatus();
        using var watching = status.Watch();
        await Until(() => _asked == 1);
        var search = NewSearch();
        using var recovery = new SearchRecovery(search, status, () => search.Type(search.Text, null, "fr"));

        search.Type("gaucher", null, "fr");
        _time.Advance(PlaceSearch.Debounce);
        await Until(() => search.Error == ApiError.Unavailable);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, _searched);
        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task Once_the_index_is_ready_what_was_typed_is_searched_again_once()
    {
        _answers.Enqueue(Services(Serving, Building(40)));
        _answers.Enqueue(Services(Serving, Building(70)));
        _answers.Enqueue(Services(Serving));
        var status = NewStatus();
        using var watching = status.Watch();
        await Until(() => _asked == 1);
        var search = NewSearch();
        using var recovery = new SearchRecovery(search, status, () => search.Type(search.Text, null, "fr"));

        search.Type("gaucher", null, "fr");
        _time.Advance(PlaceSearch.Debounce);
        await Until(() => _asked == 2);
        Assert.Equal(70, status.Geocoding!.Percent);

        _searchStatus = HttpStatusCode.OK;
        _time.Advance(ServicesStatus.PollEvery);
        await Until(() => _asked == 3);
        _time.Advance(PlaceSearch.Debounce);
        await Until(() => search.Error == ApiError.None && search.Answered);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(2, _searched);
        Assert.Equal(3, _asked);
    }

    [Fact]
    public void The_note_says_how_far_it_is_or_when_it_tries_again()
    {
        var l = new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var next = new DateTimeOffset(2026, 10, 28, 18, 5, 0, TimeSpan.Zero);
        var culture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-CA");

            Assert.Equal(
                "Trips will be ready soon: preparing Québec’s road map (45 %).",
                ServicesStatus.Note(new ServiceReadiness("building", false, "extracting", 45, null, null, null), l, "Trips"));
            Assert.Equal(
                $"Trips aren’t available right now: Québec’s road map couldn’t be prepared. Trying again at {Formatting.TimeOfDay(next, TimeZoneInfo.Local, CultureInfo.CurrentCulture)}.",
                ServicesStatus.Note(new ServiceReadiness("waiting", false, null, null, null, "the download failed", next), l, "Trips"));
            Assert.StartsWith(
                "Address search will be ready soon: preparing Québec’s addresses.",
                ServicesStatus.Note(new ServiceReadiness("starting", false, null, null, null, null, null), l, "Search"));
            Assert.Null(ServicesStatus.Note(new ServiceReadiness("waiting", true, null, null, null, "the download failed", next), l, "Trips"));
            Assert.Null(ServicesStatus.Note(null, l, "Trips"));
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        }
    }

    [Fact]
    public async Task Closing_the_map_stops_asking()
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
    public async Task Two_watchers_ask_once()
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

    private PlaceSearch NewSearch() =>
        new(new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") }), _time);

    private sealed class StubHandler(ServicesStatusTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/places/search")
            {
                Interlocked.Increment(ref test._searched);
                return Task.FromResult(test._searchStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(test._searchStatus));
            }

            Assert.Equal("/services", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref test._asked);
            var body = test._answers.Count > 0 ? test._answers.Dequeue() : Services(Serving);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
