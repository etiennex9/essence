using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class AreaTrendsTests
{
    private const string BestJson = """
        {"pick":{"id":1,"name":null,"brand":"Ultramar","address":"1 rue Principale","latitude":45.5,"longitude":-73.56,
          "priceCents":166.9,"distanceKm":0.8,"durationMin":null,"costDollars":66.99,"savesDollars":0,"membersOnly":false,
          "overDetourLimit":false,"savingUncertain":false},
         "referenceId":1,"closestId":1,"cheapestId":1,"stations":[],"radiusKm":10,"byRoad":false}
        """;

    private const string TrendsJson = """
        {"fuel":"Régulier","asOf":"2026-10-24T11:55:00Z","radiusKm":10,"stations":12,
         "now":{"at":"2026-10-24T11:55:00Z","lowestCents":158.9,"medianCents":164.9,"stations":12},
         "yesterday":null,"lastWeek":null,"low":null,"last24h":null,"jump":null,"pattern":null}
        """;

    private static readonly BestOrigin Montreal = new(new GeoPoint(45.5017, -73.5673), AccuracyMeters: 20);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly List<Uri> _requests = [];
    private HttpStatusCode _trendsStatus = HttpStatusCode.OK;
    private HttpStatusCode _probeStatus = HttpStatusCode.BadRequest;
    private TaskCompletionSource? _gate;
    private TimeSpan? _retryAfter;
    private DateTimeOffset _importedAt;
    private readonly BestNearYou _best;
    private readonly AreaTrends _trends;

    public AreaTrendsTests()
    {
        _importedAt = _time.GetUtcNow().AddMinutes(-1);
        var api = new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") });
        _best = new BestNearYou(api, new CarProfiles(new MemoryPreferences()), _time);
        _trends = new AreaTrends(api, _best, _time);
    }

    /// <summary>The trends asked for a point; the server is asked first whether it has them, without one.</summary>
    private List<Uri> TrendsRequests =>
        [.. _requests.Where(u => u.AbsolutePath.EndsWith("/stations/trends", StringComparison.Ordinal) && u.Query.Length > 0)];

    private List<Uri> Probes =>
        [.. _requests.Where(u => u.AbsolutePath.EndsWith("/stations/trends", StringComparison.Ordinal) && u.Query.Length == 0)];

    [Fact]
    public async Task Nothing_is_asked_while_the_pane_doesnt_show_Best_near_you()
    {
        await _best.SetOriginAsync(Montreal);

        Assert.Empty(TrendsRequests);
        Assert.Null(_trends.For(_best.ResultQuery));
    }

    [Fact]
    public async Task Opening_the_pane_on_an_answer_asks_once_for_its_area()
    {
        await _best.SetOriginAsync(Montreal);

        _trends.Attach();
        await _trends.RefreshAsync();

        var request = Assert.Single(TrendsRequests);
        Assert.Equal("?lat=45.5017&lon=-73.5673&fuel=R%C3%A9gulier&costco=false", request.Query);
        Assert.Single(Probes);
        Assert.Equal(164.9m, _trends.For(_best.ResultQuery)!.Now!.MedianCents);
    }

    [Fact]
    public async Task A_point_300_m_away_keeps_the_answer_and_2_km_asks_again()
    {
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);

        await _best.SetOriginAsync(Montreal with { Point = new GeoPoint(45.5040, -73.5690) });
        Assert.Single(TrendsRequests);
        Assert.NotNull(_trends.For(_best.ResultQuery));

        await _best.SetOriginAsync(Montreal with { Point = new GeoPoint(45.5200, -73.5673) });
        Assert.Equal(2, TrendsRequests.Count);
    }

    [Fact]
    public async Task Another_fuel_isnt_answered_by_the_last_answer()
    {
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);
        _trendsStatus = HttpStatusCode.InternalServerError;

        await _best.SetFuelAsync("Diesel");

        Assert.Equal(2, TrendsRequests.Count);
        Assert.Null(_trends.For(_best.ResultQuery));
    }

    [Fact]
    public async Task New_prices_or_10_minutes_ask_again()
    {
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);

        await _trends.RefreshAsync();
        Assert.Single(TrendsRequests);

        _time.Advance(TimeSpan.FromMinutes(10));
        await _trends.RefreshAsync();
        Assert.Equal(2, TrendsRequests.Count);

        _importedAt = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromMinutes(6));
        await _best.RetryAsync();
        Assert.Equal(3, TrendsRequests.Count);
    }

    [Fact]
    public async Task An_older_server_is_never_sent_the_point_nor_asked_again()
    {
        _probeStatus = HttpStatusCode.NotFound;
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);

        _time.Advance(TimeSpan.FromMinutes(30));
        await _trends.RefreshAsync();

        Assert.True(_trends.Unsupported);
        Assert.Single(Probes);
        Assert.Empty(TrendsRequests);
        Assert.Null(_trends.For(_best.ResultQuery));
    }

    [Fact]
    public async Task A_request_dropped_when_the_pane_closes_is_asked_again_when_it_opens()
    {
        await _best.SetOriginAsync(Montreal);
        _gate = new TaskCompletionSource();

        _trends.Attach();
        _trends.Detach();
        _gate.SetResult();
        await Task.Yield();
        _trends.Attach();
        await _trends.RefreshAsync();

        Assert.NotNull(_trends.For(_best.ResultQuery));
    }

    [Fact]
    public async Task An_answer_more_than_30_minutes_old_isnt_shown()
    {
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);
        _trends.Detach();

        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(_trends.For(_best.ResultQuery));
    }

    [Fact]
    public async Task A_failure_keeps_the_last_answer_and_asks_again_next_time()
    {
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);
        _trendsStatus = HttpStatusCode.InternalServerError;
        _time.Advance(TimeSpan.FromMinutes(11));
        var asked = TrendsRequests.Count;

        // A tap on a tile right after the failure doesn't ask again; 30 seconds later, Best near you's next change does.
        _best.Select(1);
        await _trends.RefreshAsync();
        Assert.Equal(asked, TrendsRequests.Count);

        _time.Advance(TimeSpan.FromSeconds(31));
        await _trends.RefreshAsync();
        Assert.Equal(asked + 1, TrendsRequests.Count);
        Assert.NotNull(_trends.For(_best.ResultQuery));
    }

    [Fact]
    public async Task Too_many_requests_waits_as_long_as_asked()
    {
        _trends.Attach();
        _trendsStatus = HttpStatusCode.TooManyRequests;
        _retryAfter = TimeSpan.FromSeconds(40);
        await _best.SetOriginAsync(Montreal);

        _time.Advance(TimeSpan.FromSeconds(30));
        await _trends.RefreshAsync();
        Assert.Single(TrendsRequests);

        _trendsStatus = HttpStatusCode.OK;
        _time.Advance(TimeSpan.FromSeconds(11));
        await _trends.RefreshAsync();
        Assert.Equal(2, TrendsRequests.Count);
    }

    [Fact]
    public async Task Closing_the_pane_stops_asking()
    {
        _trends.Attach();
        await _best.SetOriginAsync(Montreal);
        _trends.Detach();

        await _best.SetOriginAsync(Montreal with { Point = new GeoPoint(45.6, -73.5673) });

        Assert.Single(TrendsRequests);
    }

    private sealed class StubHandler(AreaTrendsTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            var trends = request.RequestUri!.AbsolutePath.EndsWith("/stations/trends", StringComparison.Ordinal);
            var probe = trends && request.RequestUri.Query.Length == 0;
            if (trends && !probe && test._gate is { } gate)
            {
                return Answer(gate.Task, request, cancellationToken);
            }

            var status = probe ? test._probeStatus : trends ? test._trendsStatus : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(trends ? TrendsJson : BestJson, Encoding.UTF8, "application/json");
                response.Content.Headers.LastModified = test._importedAt;
            }
            else if (status == HttpStatusCode.TooManyRequests && test._retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            }

            return Task.FromResult(response);
        }

        /// <summary>The answer once <paramref name="gate"/> opens: a request still on its way meanwhile, cancelled like a real one.</summary>
        private async Task<HttpResponseMessage> Answer(Task gate, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            test._gate = null;
            return await SendAsync(request, cancellationToken);
        }
    }
}
