using System.Globalization;
using System.Net;
using System.Text;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class EssenceApiClientTests
{
    /// <summary>A /stations/nearby response as the API writes it (camelCase, web defaults).</summary>
    private const string NearbyJson = """
        [{"id":42,"name":"Shell Saint-Denis","brand":"Shell","address":"1000 rue Saint-Denis","postalCode":"H2X3J4","region":"Montréal",
          "latitude":45.5155,"longitude":-73.5617,"distanceKm":1.234,
          "prices":[{"fuel":"Régulier","priceCents":167.3,"isAvailable":true,"since":"2026-10-12T19:05:01+00:00"}]}]
        """;

    [Fact]
    public async Task A_place_search_sends_what_was_typed_near_the_user_in_the_language()
    {
        var (client, requests) = Client(_ => Json(
            """[{"label":"1000 Rue De La Gauchetière Ouest","detail":"Montréal, QC H3A 4W5","latitude":45.49876,"longitude":-73.56657,"kind":"address"}]"""));

        var result = await client.SearchPlacesAsync("1000 de la gauch & co", new GeoPoint(45.50171, -73.56732), "fr", TestContext.Current.CancellationToken);

        Assert.Equal("1000 Rue De La Gauchetière Ouest", Assert.Single(result.Value!).Label);
        Assert.Equal("/places/search?q=1000%20de%20la%20gauch%20%26%20co&lat=45.5017&lon=-73.5673&lang=fr", Assert.Single(requests).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task A_place_search_s_numbers_are_invariant_in_French()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
        try
        {
            var (client, requests) = Client(_ => Json("[]"));

            await client.SearchPlacesAsync("rue gaucher", new GeoPoint(45.5248, -73.58), "fr", TestContext.Current.CancellationToken);
            await client.GetPlaceAsync(new GeoPoint(45.5248, -73.58), "fr", TestContext.Current.CancellationToken);

            Assert.Equal("?q=rue%20gaucher&lat=45.5248&lon=-73.58&lang=fr", requests[0].RequestUri!.Query);
            Assert.Equal("?lat=45.5248&lon=-73.58&lang=fr", requests[1].RequestUri!.Query);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, ApiError.NoRoute)]
    [InlineData(HttpStatusCode.NotFound, ApiError.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiError.Unavailable)]
    public async Task A_point_outside_Québec_or_without_an_address_is_told_apart(HttpStatusCode status, ApiError error)
    {
        var (client, requests) = Client(_ => new HttpResponseMessage(status));

        var result = await client.GetPlaceAsync(new GeoPoint(45.426, -75.695), "en", TestContext.Current.CancellationToken);

        Assert.Equal(error, result.Error);
        Assert.Equal("/places/reverse?lat=45.426&lon=-75.695&lang=en", Assert.Single(requests).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Nearby_deserializes_the_api_shape()
    {
        var (client, _) = Client(_ => Json(NearbyJson));

        var result = await client.GetNearbyAsync(45.5, -73.56, 3, "Régulier", 20, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var station = Assert.Single(result.Value!);
        Assert.Equal(42, station.Id);
        Assert.Equal(167.3m, station.Prices[0].PriceCents);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 19, 5, 1, TimeSpan.Zero), station.Prices[0].Since);
    }

    [Fact]
    public async Task Last_modified_is_when_the_data_was_imported()
    {
        var importedAt = new DateTimeOffset(2026, 10, 16, 12, 5, 41, TimeSpan.Zero);
        var (client, _) = Client(_ =>
        {
            var response = Json(NearbyJson);
            response.Content.Headers.LastModified = importedAt;
            return response;
        });

        var result = await client.GetNearbyAsync(45.5, -73.56, 3, "Régulier", 20, TestContext.Current.CancellationToken);

        Assert.Equal(importedAt, result.DataAsOf);
    }

    [Fact]
    public async Task Without_last_modified_the_version_is_unknown()
    {
        var (client, _) = Client(_ => Json(NearbyJson));
        var result = await client.GetNearbyAsync(45.5, -73.56, 3, "Régulier", 20, TestContext.Current.CancellationToken);
        Assert.Null(result.DataAsOf);
    }

    [Fact]
    public async Task Query_numbers_are_invariant_whatever_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
        try
        {
            var (client, requests) = Client(_ => Json("[]"));
            await client.GetNearbyAsync(45.5017, -73.5673, 2.5, "Régulier", 100, TestContext.Current.CancellationToken);

            var uri = Assert.Single(requests).RequestUri!;
            Assert.Equal("/stations/nearby", uri.AbsolutePath);
            Assert.Contains("lat=45.5017&lon=-73.5673&radiusKm=2.5", uri.Query);
            Assert.Contains("fuel=R%C3%A9gulier", uri.Query);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Rate_limit_carries_retry_after()
    {
        var (client, _) = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        });

        var result = await client.GetStationAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(ApiError.RateLimited, result.Error);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ApiError.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, ApiError.Invalid)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ApiError.NoRoute)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiError.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ApiError.Failed)]
    [InlineData(HttpStatusCode.BadGateway, ApiError.Failed)]
    public async Task Error_statuses_map_to_errors(HttpStatusCode status, ApiError expected)
    {
        var (client, _) = Client(_ => new HttpResponseMessage(status));
        var result = await client.GetStationAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task Best_sends_the_car_and_reads_the_ranking()
    {
        const string json = """
            {"pick":{"id":2,"name":"Costco Langelier","brand":"Costco","address":"7373 boul. Langelier","latitude":45.58,"longitude":-73.56,
              "priceCents":155.9,"distanceKm":9.0,"durationMin":null,"costDollars":64.75,"savesDollars":2.24,"membersOnly":true,
              "overDetourLimit":false,"savingUncertain":false},
             "referenceId":1,"closestId":1,"cheapestId":2,"stations":[],"radiusKm":null,"byRoad":false}
            """;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
        try
        {
            var (client, requests) = Client(_ => Json(json));

            var result = await client.GetBestAsync(
                new BestQuery(45.5017, -73.5673, "Régulier", 40, 8.5m, true, 10), TestContext.Current.CancellationToken);

            var uri = Assert.Single(requests).RequestUri!;
            Assert.Equal("/stations/best", uri.AbsolutePath);
            Assert.Equal(
                "?lat=45.5017&lon=-73.5673&fuel=R%C3%A9gulier&fillL=40&per100Km=8.5&costco=true&maxDetourMin=10", uri.Query);
            var best = result.Value!;
            Assert.Equal(2.24m, best.Pick!.SavesDollars);
            Assert.True(best.Pick.MembersOnly);
            Assert.Null(best.Pick.DurationMin);
            Assert.Null(best.RadiusKm);
            Assert.Equal(1, best.ReferenceId);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Network_failure_is_an_error_not_an_exception()
    {
        var (client, _) = Client(_ => throw new HttpRequestException("connection refused"));
        var result = await client.GetPriceHistoryAsync(
            1, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(ApiError.Failed, result.Error);
    }

    [Fact]
    public async Task History_range_is_sent_in_utc_iso_format()
    {
        var (client, requests) = Client(_ => Json("[]"));
        var from = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(-4));

        await client.GetPriceHistoryAsync(7, from, from.AddDays(7), TestContext.Current.CancellationToken);

        var query = Uri.UnescapeDataString(Assert.Single(requests).RequestUri!.Query);
        Assert.Contains("from=2026-10-01T12:00:00.0000000Z", query);
        Assert.Contains("to=2026-10-08T12:00:00.0000000Z", query);
    }

    private static (EssenceApiClient Client, List<HttpRequestMessage> Requests) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        return (new EssenceApiClient(http), handler.Requests);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
