using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Essence.Contracts;
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

    /// <summary>Hours as the API writes them: Costco's, checked by Essence, temporarily closed left at its default.</summary>
    private const string HoursJson = """
        {"rule":"Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30","source":"curated","trusted":true,"confirmed":true,
         "checkedAt":"2026-11-11T00:00:00-05:00","link":"https://www.costco.ca/w/-/locations"}
        """;

    private static readonly StationHours Hours = new(
        "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", "curated", Trusted: true, Confirmed: true,
        new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(-5)), "https://www.costco.ca/w/-/locations");

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
        // An API from before the hours sends none: unknown.
        Assert.Null(station.Hours);
    }

    [Fact]
    public async Task Nearby_reads_the_stations_hours()
    {
        var (client, _) = Client(_ => Json(NearbyJson.Replace("\"distanceKm\":1.234,", $"\"distanceKm\":1.234,\"hours\":{HoursJson},")));

        var result = await client.GetNearbyAsync(45.5, -73.56, 3, "Régulier", 20, TestContext.Current.CancellationToken);

        Assert.Equal(Hours, Assert.Single(result.Value!).Hours);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_station_s_details_read_with_their_hours_or_without(bool withHours)
    {
        var json = $$"""
            {"id":42,"name":"Costco Laval","brand":"Costco","status":"En opération","address":"1 rue Principale",
             "postalCode":"H7T0A1","region":"Laval","latitude":45.5699,"longitude":-73.7469,"isActive":true,
             "firstSeenAt":"2026-10-01T00:00:00Z","lastSeenAt":"2026-11-11T00:00:00Z","updatedAt":"2026-11-11T00:00:00Z",
             "prices":[]{{(withHours ? $",\"hours\":{HoursJson}" : "")}}}
            """;
        var (client, _) = Client(_ => Json(json));

        var result = await client.GetStationAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(withHours ? Hours : null, result.Value!.Hours);
    }

    [Theory]
    [InlineData(",\"osmElement\":\"way/456\"", "way/456")]
    [InlineData(",\"osmElement\":null", null)]
    [InlineData("", null)]
    public async Task A_station_s_details_read_its_OpenStreetMap_element_or_none_from_an_older_API(string field, string? element)
    {
        var json = $$"""
            {"id":42,"name":"Costco Laval","brand":"Costco","status":"En opération","address":"1 rue Principale",
             "postalCode":"H7T0A1","region":"Laval","latitude":45.5699,"longitude":-73.7469,"isActive":true,
             "firstSeenAt":"2026-10-01T00:00:00Z","lastSeenAt":"2026-11-11T00:00:00Z","updatedAt":"2026-11-11T00:00:00Z",
             "prices":[],"hours":null{{field}}}
            """;
        var (client, _) = Client(_ => Json(json));

        var result = await client.GetStationAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(element, result.Value!.OsmElement);
    }

    [Theory]
    [InlineData(",\"osmElement\":\"way/456\",\"osmBrandDiffers\":true", true)]
    [InlineData(",\"osmElement\":\"way/456\",\"osmBrandDiffers\":false", false)]
    [InlineData(",\"osmElement\":null,\"osmBrandDiffers\":null", null)]
    [InlineData(",\"osmElement\":\"way/456\"", null)]
    [InlineData("", null)]
    public async Task A_station_s_details_say_whether_its_element_was_matched_by_place_only_or_not_from_an_older_API(string fields, bool? differs)
    {
        var json = $$"""
            {"id":42,"name":"Nutrinor Energies","brand":"Nutrinor Énergies","status":"En opération","address":"1 rue Principale",
             "postalCode":"G7H0A1","region":"Saguenay-Lac-Saint-Jean","latitude":48.42,"longitude":-71.07,"isActive":true,
             "firstSeenAt":"2026-10-01T00:00:00Z","lastSeenAt":"2026-11-11T00:00:00Z","updatedAt":"2026-11-11T00:00:00Z",
             "prices":[],"hours":null{{fields}}}
            """;
        var (client, _) = Client(_ => Json(json));

        var result = await client.GetStationAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(differs, result.Value!.OsmBrandDiffers);
    }

    [Theory]
    [InlineData(",\"osmElement\":\"node/5310653589\",\"osmBrandDiffers\":true,\"osmBrand\":\"Sonic\"", "Sonic")]
    [InlineData(",\"osmElement\":\"way/221282039\",\"osmBrandDiffers\":true,\"osmBrand\":null", null)]
    [InlineData(",\"osmElement\":\"way/456\",\"osmBrandDiffers\":true", null)]
    [InlineData("", null)]
    public async Task A_station_s_details_say_the_brand_read_on_its_element_or_nothing_from_an_older_API(string fields, string? brand)
    {
        var json = $$"""
            {"id":42,"name":"Nutrinor Energies","brand":"Nutrinor Énergies","status":"En opération","address":"253 rue Principale",
             "postalCode":"G0W2V0","region":"Saguenay-Lac-Saint-Jean","latitude":48.58,"longitude":-71.55,"isActive":true,
             "firstSeenAt":"2026-10-01T00:00:00Z","lastSeenAt":"2026-11-11T00:00:00Z","updatedAt":"2026-11-11T00:00:00Z",
             "prices":[],"hours":null{{fields}}}
            """;
        var (client, _) = Client(_ => Json(json));

        var result = await client.GetStationAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(brand, result.Value!.OsmBrand);
    }

    [Fact]
    public void An_older_app_reads_the_details_as_before_whether_its_element_was_matched_by_place_only_or_not()
    {
        var station = TestStations.Detail(42, "Nutrinor Énergies") with { OsmElement = "way/456", OsmBrandDiffers = true, OsmBrand = "Sonic" };
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(station, web);
        var older = JsonSerializer.Deserialize<OlderStationDetail>(json, web);

        Assert.EndsWith(""","osmElement":"way/456","osmBrandDiffers":true,"osmBrand":"Sonic"}""", json);
        Assert.Equal(new OlderStationDetail(42, "Nutrinor Énergies", "way/456"), older);
    }

    /// <summary>
    /// The details as an app from before <see cref="StationDetail.OsmBrandDiffers"/> and <see cref="StationDetail.OsmBrand"/>
    /// reads them (the fields it needs).
    /// </summary>
    private sealed record OlderStationDetail(long Id, string? Brand, string? OsmElement = null);

    [Fact]
    public async Task The_server_s_settings_name_its_OpenStreetMap_application_and_an_older_server_has_none()
    {
        var (client, requests) = Client(r => r.RequestUri!.AbsolutePath == "/client-config"
            ? Json("""{"osmClientId":"abc123","osmAuthUrl":"https://www.openstreetmap.org","osmApiUrl":"https://api.openstreetmap.org"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var config = await client.GetClientConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ClientConfig("abc123", "https://www.openstreetmap.org", "https://api.openstreetmap.org"), config.Value);
        Assert.Equal("/client-config", Assert.Single(requests).RequestUri!.AbsolutePath);

        var (older, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal(ApiError.NotFound, (await older.GetClientConfigAsync(TestContext.Current.CancellationToken)).Error);
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

            // An older API's: no hours, ETA, or stations left out.
            Assert.Null(best.Pick.Hours);
            Assert.Null(best.Pick.EtaMin);
            Assert.Null(best.LeftOut);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Best_reads_the_hours_the_ETA_and_the_stations_left_out()
    {
        var json = $$"""
            {"pick":{"id":1,"name":null,"brand":"Ultramar","address":"1 rue Principale","latitude":45.5,"longitude":-73.56,
              "priceCents":164.9,"distanceKm":1.2,"durationMin":3.1,"costDollars":66.01,"savesDollars":0,"membersOnly":false,
              "overDetourLimit":false,"savingUncertain":false,"hours":{{HoursJson}},"etaMin":3.1},
             "referenceId":1,"closestId":1,"cheapestId":1,"stations":[],"radiusKm":10,"byRoad":true,
             "leftOut":[{"id":2,"name":"Costco Laval","brand":"Costco","address":"2999 Autoroute Chomedey","latitude":45.5699,
               "longitude":-73.7469,"priceCents":153.9,"hours":{{HoursJson}},"etaMin":12.4}]}
            """;
        var (client, _) = Client(_ => Json(json));

        var best = (await client.GetBestAsync(new BestQuery(45.5, -73.56, "Régulier", 40, 8.5m, true, 10), TestContext.Current.CancellationToken)).Value!;

        Assert.Equal((Hours, (double?)3.1), (best.Pick!.Hours, best.Pick.EtaMin));
        Assert.Equal(
            new LeftOutStation(2, "Costco Laval", "Costco", "2999 Autoroute Chomedey", 45.5699, -73.7469, 153.9m, Hours, 12.4),
            Assert.Single(best.LeftOut!));
    }

    [Fact]
    public async Task A_trip_reads_each_station_s_hours_and_time_from_the_start_or_none_from_an_older_API()
    {
        const string station = """
            {"id":3,"name":null,"brand":"Petro-Canada","address":"1 boul. Saint-Joseph","latitude":45.88,"longitude":-72.48,
             "priceCents":159.9,"kmAlong":105,"distanceKm":107,"detourKm":4,"detourMin":5,"costDollars":64.50,"savesDollars":1.26,
             "membersOnly":false,"reachable":true,"overDetourLimit":false
            """;
        const string plan = """
            {"path":[[-73.5673,45.5017],[-71.208,46.8139]],"distanceKm":250.3,"durationMin":155.2,"halfWidthKm":5,"longTrip":true,
             "stopBeforeKm":null,"lowFuel":false,"stations":[STATION],"pickId":3,"referenceId":3,"referenceOnTheWay":true,
             "biggestSavingId":nullMORE}
            """;
        var newer = plan
            .Replace("STATION", $$"""{{station}},"hours":{{HoursJson}},"toMin":61.5}""")
            .Replace("MORE", $$""","leftOut":[{"id":6,"name":null,"brand":"Costco","address":"a","latitude":46,"longitude":-72,"priceCents":154.9,"hours":{{HoursJson}},"etaMin":130}]""");
        var older = plan.Replace("STATION", station + "}").Replace("MORE", "");
        var query = new TripQuery(45.5017, -73.5673, 46.8139, -71.208, "Régulier", 40, 8.5m, false, 10, null, 30);

        var (newClient, _) = Client(_ => Json(newer));
        var (oldClient, _) = Client(_ => Json(older));
        var trip = (await newClient.GetTripAsync(query, TestContext.Current.CancellationToken)).Value!;
        var old = (await oldClient.GetTripAsync(query, TestContext.Current.CancellationToken)).Value!;

        Assert.Equal((Hours, (double?)61.5), (trip.Stations[0].Hours, trip.Stations[0].ToMin));
        Assert.Equal((6L, 130.0), (Assert.Single(trip.LeftOut!).Id, trip.LeftOut![0].EtaMin));
        Assert.Null(old.Stations[0].Hours);
        Assert.Null(old.Stations[0].ToMin);
        Assert.Null(old.LeftOut);
    }

    [Fact]
    public async Task The_stations_whose_hours_to_ignore_are_sent_once_each_and_none_sends_nothing()
    {
        var (client, requests) = Client(_ => Json("{}"));
        var best = new BestQuery(45.5017, -73.5673, "Régulier", 40, 8.5m, false, 10);
        var trip = new TripQuery(45.5017, -73.5673, 46.8139, -71.208, "Régulier", 40, 8.5m, false, 10, null, 30);

        await client.GetBestAsync(best, [42, 7, 42], TestContext.Current.CancellationToken);
        await client.GetBestAsync(best, [], TestContext.Current.CancellationToken);
        await client.GetTripAsync(trip, [42], TestContext.Current.CancellationToken);
        await client.GetTripAsync(trip, TestContext.Current.CancellationToken);
        await client.GetBestAsync(best, [.. Enumerable.Range(1, 60).Select(i => (long)i)], TestContext.Current.CancellationToken);

        Assert.EndsWith("&maxDetourMin=10&ignoreHours=42,7", requests[0].RequestUri!.Query);
        Assert.DoesNotContain("ignoreHours", requests[1].RequestUri!.Query);
        Assert.EndsWith("&spareKm=30&ignoreHours=42", requests[2].RequestUri!.Query);
        Assert.DoesNotContain("ignoreHours", requests[3].RequestUri!.Query);
        Assert.EndsWith($"&ignoreHours={string.Join(',', Enumerable.Range(1, 50))}", requests[4].RequestUri!.Query);
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
