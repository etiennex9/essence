using System.Net;
using System.Text;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class HistoryCacheTests
{
    private const string HistoryJson = """
        [{"fuel":"Régulier","priceCents":164.9,"isAvailable":true,"validFrom":"2026-10-10T12:00:00+00:00","validTo":null}]
        """;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly List<HttpRequestMessage> _requests = [];
    private HttpStatusCode _status = HttpStatusCode.OK;

    [Fact]
    public async Task A_fresh_history_is_reused()
    {
        var cache = Cache();

        var first = await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);

        Assert.Single(_requests);
        Assert.Same(first.Value, second.Value);
        Assert.Equal(164.9m, Assert.Single(second.Value!.Rows).PriceCents);
        Assert.Equal(_time.GetUtcNow().AddMinutes(-1).AddDays(-30), second.Value.From);
    }

    [Fact]
    public async Task Another_period_or_station_is_fetched()
    {
        var cache = Cache();

        await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);
        await cache.GetAsync(7, 7, TestContext.Current.CancellationToken);
        await cache.GetAsync(8, 30, TestContext.Current.CancellationToken);

        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public async Task A_stale_history_is_fetched_again()
    {
        var cache = Cache();

        await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);
        _time.Advance(HistoryCache.Freshness);
        await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);

        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task Failures_are_not_kept()
    {
        var cache = Cache();
        _status = HttpStatusCode.InternalServerError;

        var failed = await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);
        _status = HttpStatusCode.OK;
        var retried = await cache.GetAsync(7, 30, TestContext.Current.CancellationToken);

        Assert.Equal(ApiError.Failed, failed.Error);
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, _requests.Count);
    }

    private HistoryCache Cache()
    {
        var http = new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") };
        return new HistoryCache(new EssenceApiClient(http), _time);
    }

    private sealed class StubHandler(HistoryCacheTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request);
            var response = new HttpResponseMessage(test._status);
            if (test._status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(HistoryJson, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
