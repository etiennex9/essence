using System.Net;
using System.Net.Http.Json;
using System.Text;
using Essence.Contracts;
using Essence.Ranking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Essence.Api.Tests;

/// <summary>The address search endpoints against a stub Photon; no database is involved.</summary>
public sealed class PlaceEndpointsTests : IDisposable
{
    private const string Montreal = """
        {"type":"Feature","properties":{"osm_key":"place","osm_value":"house","type":"house","housenumber":"1000",
          "street":"Rue De La Gauchetière Ouest","district":"Ville-Marie","city":"Montréal","state":"Québec",
          "postcode":"H3A 4W5","countrycode":"CA"},"geometry":{"type":"Point","coordinates":[-73.56657,45.49876]}}
        """;

    private const string Ottawa = """
        {"type":"Feature","properties":{"osm_key":"place","osm_value":"house","type":"house","housenumber":"100",
          "street":"Queen Street","city":"Ottawa","state":"Ontario","postcode":"K1P 1A4","countrycode":"CA"},
         "geometry":{"type":"Point","coordinates":[-75.69751,45.42168]}}
        """;

    private readonly List<Uri> _requests = [];
    private readonly WebApplicationFactory<Program> _factory;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Features(Montreal);

    public PlaceEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // The places endpoints don't use the database; the API only needs a connection string to start.
            builder.UseSetting("ConnectionStrings:Essence", "Host=localhost;Database=unused");
            builder.UseSetting("RateLimiting:Search:PermitLimit", "5");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<PhotonClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this)));
        });
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task A_search_suggests_Québec_s_places_only()
    {
        _respond = _ => Features(Ottawa, Montreal);

        var suggestions = await Client.GetFromJsonAsync<List<PlaceSuggestion>>(
            "/places/search?q=1000%20de%20la%20gauch&lat=45.5017&lon=-73.5673&lang=fr", TestContext.Current.CancellationToken);

        var suggestion = Assert.Single(suggestions!);
        Assert.Equal(new PlaceSuggestion("1000 Rue De La Gauchetière Ouest", "Montréal, QC H3A 4W5", 45.49876, -73.56657, "address"), suggestion);
        Assert.StartsWith("/api?q=1000%20de%20la%20gauch&lang=fr&limit=20&bbox=-79.9,44.9,-57,62.7&lat=45.5017&lon=-73.5673",
            Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task A_search_for_an_Ontario_address_suggests_nothing()
    {
        _respond = _ => Features(Ottawa);

        var suggestions = await Client.GetFromJsonAsync<List<PlaceSuggestion>>(
            "/places/search?q=100%20queen%20street%20ottawa&lang=en", TestContext.Current.CancellationToken);

        Assert.Empty(suggestions!);
    }

    [Fact]
    public async Task What_was_typed_is_trimmed_and_its_spaces_collapsed_and_French_is_the_default()
    {
        await Client.GetAsync("/places/search?q=%20%201000%20%20%20de%20la%20", TestContext.Current.CancellationToken);

        Assert.StartsWith("/api?q=1000%20de%20la&lang=fr&", Assert.Single(_requests).PathAndQuery);
    }

    [Theory]
    [InlineData("/places/search", "q")]
    [InlineData("/places/search?q=ab", "q")]
    [InlineData("/places/search?q=%20%20ab%20%20", "q")]
    [InlineData("/places/search?q=gaucher&lat=45.5", "lat")]
    [InlineData("/places/search?q=gaucher&lat=95&lon=-73", "lat")]
    [InlineData("/places/search?q=gaucher&lang=de", "lang")]
    [InlineData("/places/reverse?lat=45.5&lon=-200", "lon")]
    [InlineData("/places/reverse?lat=45.5&lon=-73.6&lang=es", "lang")]
    public async Task A_bad_request_is_refused_before_Photon(string path, string parameter)
    {
        var response = await Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{parameter}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Control_characters_count_as_spaces_and_a_blank_search_is_refused()
    {
        var blank = await Client.GetAsync("/places/search?q=%1C%1C%1C", TestContext.Current.CancellationToken);
        await Client.GetAsync("/places/search?q=rue%09gaucher%0A", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.StartsWith("/api?q=rue%20gaucher&", Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task A_search_typed_too_long_is_refused()
    {
        var response = await Client.GetAsync($"/places/search?q={new string('a', 101)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_pin_gets_its_address()
    {
        var place = await Client.GetFromJsonAsync<PlaceSuggestion>(
            "/places/reverse?lat=45.4988&lon=-73.5666&lang=en", TestContext.Current.CancellationToken);

        Assert.Equal("1000 Rue De La Gauchetière Ouest", place!.Label);
        Assert.StartsWith("/reverse?lat=45.4988&lon=-73.5666&lang=en&radius=1&limit=20", Assert.Single(_requests).PathAndQuery);
    }

    [Theory]
    [InlineData(45.4260, -75.6950)] // Downtown Ottawa
    [InlineData(45.0030, -72.0990)] // Derby Line, Vermont
    public async Task A_pin_past_the_border_is_outside_Québec_without_asking_Photon(double latitude, double longitude)
    {
        var response = await Client.GetAsync(
            FormattableString.Invariant($"/places/reverse?lat={latitude}&lon={longitude}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Outside Québec", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task A_pin_near_the_border_isn_t_named_after_the_neighbour()
    {
        _respond = _ => Features(Ottawa);

        var response = await Client.GetAsync("/places/reverse?lat=45.4330&lon=-75.7050", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_pin_with_nothing_near_has_no_address()
    {
        _respond = _ => Features();

        var response = await Client.GetAsync("/places/reverse?lat=46.5&lon=-72.6", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Photon_down_is_503_for_both()
    {
        _respond = _ => throw new HttpRequestException("Connection refused");

        var search = await Client.GetAsync("/places/search?q=gaucher", TestContext.Current.CancellationToken);
        var reverse = await Client.GetAsync("/places/reverse?lat=45.5&lon=-73.6", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, search.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reverse.StatusCode);

        // Once each: no retry.
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task Photon_down_makes_the_health_check_degraded()
    {
        var registration = new HealthCheckRegistration("geocoding", _ => null!, HealthStatus.Degraded, null);
        var photon = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<PhotonClient>();
        var check = new GeocodingHealthCheck(photon);

        _respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"Ok"}""", Encoding.UTF8, "application/json"),
        };
        var up = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration }, TestContext.Current.CancellationToken);
        _respond = _ => throw new HttpRequestException("Connection refused");
        var down = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration }, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, up.Status);
        Assert.Equal(HealthStatus.Degraded, down.Status);
    }

    [Fact]
    public async Task Searches_and_pins_share_their_own_rate_limit()
    {
        var client = Client;
        var statuses = new List<HttpStatusCode>();
        foreach (var path in (string[])["/places/search?q=gaucher", "/places/reverse?lat=45.5&lon=-73.6"])
        {
            for (var i = 0; i < 3; i++)
            {
                statuses.Add((await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
            }
        }

        // RateLimiting:Search:PermitLimit is 5 here; the shared limit is 120.
        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.OK, 5), HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task What_was_typed_and_where_stay_out_of_the_logs()
    {
        var logs = new CapturedLogs();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        var client = factory.CreateClient();
        _respond = request => request.RequestUri!.AbsolutePath == "/api" ? throw new HttpRequestException("Connection refused") : Features(Montreal);

        await client.GetAsync("/places/search?q=Rue%20Secr%C3%A8te&lat=45.5017&lon=-73.5673", TestContext.Current.CancellationToken);
        await client.GetAsync("/places/reverse?lat=45.4988&lon=-73.5666", TestContext.Current.CancellationToken);

        Assert.Contains(logs.Messages, m => m.Contains("The address search is unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("Secr", StringComparison.OrdinalIgnoreCase)
            || m.Contains("45.50", StringComparison.Ordinal) || m.Contains("73.56", StringComparison.Ordinal)
            || m.Contains("45.49", StringComparison.Ordinal));
    }

    public void Dispose() => _factory.Dispose();

    private static HttpResponseMessage Features(params string[] features) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"type":"FeatureCollection","features":[{{string.Join(',', features)}}]}""", Encoding.UTF8, "application/json"),
        };

    /// <summary>Every message logged at the levels the API's configuration lets through.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs logs, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (logs._messages)
                {
                    logs._messages.Add($"{category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }

    private sealed class StubHandler(PlaceEndpointsTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (test._requests)
            {
                test._requests.Add(request.RequestUri!);
            }

            return Task.FromResult(test._respond(request));
        }
    }
}
