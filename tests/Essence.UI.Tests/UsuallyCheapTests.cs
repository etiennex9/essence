using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class UsuallyCheapTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly List<Uri> _requests = [];
    private HttpStatusCode _status = HttpStatusCode.OK;
    private TimeSpan? _retryAfter;
    private TaskCompletionSource? _gate;
    private readonly UsuallyCheap _cheap;
    private int _changes;

    public UsuallyCheapTests()
    {
        var api = new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") });
        _cheap = new UsuallyCheap(api, _time);
        _cheap.Changed += () => _changes++;
    }

    [Fact]
    public void A_station_is_usually_cheap_once_its_fuel_s_answer_is_in()
    {
        _gate = new TaskCompletionSource();

        Assert.False(_cheap.Is(3, "Régulier"));
        Assert.False(_cheap.Is(17, "Régulier"));
        _gate.SetResult();

        Assert.Single(_requests);
        Assert.Equal("?fuel=R%C3%A9gulier", _requests[0].Query);
        Assert.Equal(1, _changes);
        Assert.True(_cheap.Is(3, "Régulier"));
        Assert.True(_cheap.Is(3, "régulier"));
        Assert.False(_cheap.Is(4, "Régulier"));
    }

    [Fact]
    public void Each_fuel_is_asked_once_then_again_ten_minutes_later()
    {
        _cheap.Is(3, "Régulier");
        _cheap.Is(3, "Diesel");
        _time.Advance(TimeSpan.FromMinutes(9));
        _cheap.Is(3, "Régulier");

        Assert.Equal(2, _requests.Count);

        _time.Advance(TimeSpan.FromMinutes(1));
        _cheap.Is(3, "Régulier");

        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public void A_server_without_it_is_not_asked_again_and_no_station_is()
    {
        _status = HttpStatusCode.NotFound;

        _cheap.Is(3, "Régulier");
        _time.Advance(TimeSpan.FromDays(1));
        _cheap.Is(3, "Diesel");

        Assert.Single(_requests);
        Assert.True(_cheap.Unsupported);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void A_failure_keeps_the_last_answer_and_waits_a_minute()
    {
        _cheap.Is(3, "Régulier");
        _status = HttpStatusCode.InternalServerError;
        _time.Advance(UsuallyCheap.MaxAge);

        _cheap.Is(3, "Régulier");
        _cheap.Is(3, "Régulier");

        Assert.Equal(2, _requests.Count);
        Assert.True(_cheap.Is(3, "Régulier"));

        _time.Advance(UsuallyCheap.RetryAfterFailure);
        _cheap.Is(3, "Régulier");

        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public void Too_many_requests_waits_as_long_as_the_server_says()
    {
        _status = HttpStatusCode.TooManyRequests;
        _retryAfter = TimeSpan.FromMinutes(5);

        _cheap.Is(3, "Régulier");
        _time.Advance(TimeSpan.FromMinutes(4));
        _cheap.Is(3, "Régulier");

        Assert.Single(_requests);

        _time.Advance(TimeSpan.FromMinutes(1));
        _cheap.Is(3, "Régulier");

        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public void No_fuel_asks_nothing()
    {
        Assert.False(_cheap.Is(3, null));
        Assert.Empty(_requests);
    }

    private sealed class StubHandler(UsuallyCheapTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            return test._gate is { } gate ? Answer(gate.Task) : Task.FromResult(Response());
        }

        private async Task<HttpResponseMessage> Answer(Task gate)
        {
            await gate;
            return Response();
        }

        private HttpResponseMessage Response()
        {
            var response = new HttpResponseMessage(test._status);
            if (test._status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    """{"fuel":"Régulier","asOf":"2026-11-26T11:55:00Z","days":30,"ids":[3,17]}""", Encoding.UTF8, "application/json");
            }
            else if (test._status == HttpStatusCode.TooManyRequests && test._retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            }

            return response;
        }
    }
}
