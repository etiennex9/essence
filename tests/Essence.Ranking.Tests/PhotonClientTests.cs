using System.Net;
using System.Text;

namespace Essence.Ranking.Tests;

public class PhotonClientTests
{
    private const string OneHouse = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","properties":{"osm_type":"N","osm_id":2439183785,"osm_key":"place","osm_value":"house","type":"house",
            "housenumber":"1000","street":"Rue De La Gauchetière Ouest","district":"Ville-Marie","city":"Montréal",
            "county":"Agglomération de Montréal","state":"Québec","country":"Canada","postcode":"H3A 4W5","countrycode":"CA"},
           "geometry":{"type":"Point","coordinates":[-73.56657,45.49876]}}]}
        """;

    private readonly List<Uri> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Json(HttpStatusCode.OK, OneHouse);

    private PhotonClient Client => new(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://geocoding.test:2322/") });

    [Fact]
    public async Task A_search_asks_within_Québec_s_box_near_the_user_for_places_to_drive_to()
    {
        var places = await Client.SearchAsync(
            new PhotonSearch("1000 de la gauch", "fr", new LatLon(45.5017, -73.5673), 12), TestContext.Current.CancellationToken);

        var place = Assert.Single(places);
        Assert.Equal(new LatLon(45.49876, -73.56657), place.Location);
        Assert.Equal(("house", "1000", "Rue De La Gauchetière Ouest", "H3A 4W5"), (place.Type, place.HouseNumber, place.Street, place.Postcode));
        Assert.Equal(("Ville-Marie", "Montréal", "Québec", "CA"), (place.District, place.City, place.State, place.CountryCode));
        Assert.Equal(
            "/api?q=1000%20de%20la%20gauch&lang=fr&limit=12&bbox=-79.9,44.9,-57,62.7&lat=45.5017&lon=-73.5673"
            + "&layer=house&layer=street&layer=locality&layer=district&layer=city&layer=other"
            + "&osm_tag=%21waterway&osm_tag=%21natural&osm_tag=%21landuse&osm_tag=%21boundary",
            Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task A_search_without_a_location_has_no_bias()
    {
        await Client.SearchAsync(new PhotonSearch("Rue & Cie", "en", null, 5), TestContext.Current.CancellationToken);

        var query = Assert.Single(_requests).Query;
        Assert.StartsWith("?q=Rue%20%26%20Cie&lang=en&limit=5&bbox=", query);
        Assert.DoesNotContain("&lat=", query);
    }

    [Fact]
    public async Task A_reverse_lookup_asks_for_the_nearest_addresses_streets_and_places()
    {
        var places = await Client.ReverseAsync(new LatLon(45.4988, -73.5666), "en", 1, 5, TestContext.Current.CancellationToken);

        Assert.Single(places);
        Assert.Equal(
            "/reverse?lat=45.4988&lon=-73.5666&lang=en&radius=1&limit=5&layer=house&layer=street&layer=other"
            + "&osm_tag=%21waterway&osm_tag=%21natural&osm_tag=%21landuse&osm_tag=%21boundary",
            Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task Nothing_found_is_no_places()
    {
        _respond = _ => Json(HttpStatusCode.OK, """{"type":"FeatureCollection","features":[]}""");

        Assert.Empty(await Client.ReverseAsync(new LatLon(46.5, -72.6), "fr", 1, 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_feature_without_a_point_is_skipped_and_missing_fields_are_null()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"features":[{"properties":{"name":"Nowhere"}},
                         {"properties":{"name":"  Laval ","type":"city","state":""},"geometry":{"coordinates":[-73.7,45.6]}}]}
            """);

        var place = Assert.Single(PhotonClient.Parse(document.RootElement));

        Assert.Equal(("city", "Laval", null, null), (place.Type, place.Name, place.State, place.Street));
    }

    [Fact]
    public async Task The_status_is_ok_once_the_index_is_loaded()
    {
        _respond = _ => Json(HttpStatusCode.OK, """{"status":"Ok","import_date":"2026-10-17T23:02:06Z","version":"1.3.0"}""");

        await Client.StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/status", Assert.Single(_requests).PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, PhotonFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, PhotonFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, PhotonFailure.Bug)]
    public async Task An_error_status_is_a_failure(HttpStatusCode status, PhotonFailure failure)
    {
        _respond = _ => Json(status, """{"message":"Language is not supported. Supported are: default, en, fr"}""");

        var error = await Assert.ThrowsAsync<PhotonException>(
            () => Client.SearchAsync(new PhotonSearch("gaucher", "de", null, 5), TestContext.Current.CancellationToken));

        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("gaucher", error.Message);
    }

    [Fact]
    public async Task No_answer_is_unavailable()
    {
        _respond = _ => throw new HttpRequestException("Name or service not known (geocoding:2322)");

        var error = await Assert.ThrowsAsync<PhotonException>(
            () => Client.ReverseAsync(new LatLon(45.5, -73.6), "fr", 1, 5, TestContext.Current.CancellationToken));

        Assert.Equal(PhotonFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task An_answer_that_isn_t_its_GeoJSON_is_a_bug()
    {
        _respond = _ => Json(HttpStatusCode.OK, "<html>starting</html>");

        var error = await Assert.ThrowsAsync<PhotonException>(
            () => Client.SearchAsync(new PhotonSearch("gaucher", "fr", null, 5), TestContext.Current.CancellationToken));

        Assert.Equal(PhotonFailure.Bug, error.Failure);
    }

    [Fact]
    public async Task A_status_that_isn_t_ok_is_unavailable()
    {
        _respond = _ => Json(HttpStatusCode.OK, """{"status":"Starting"}""");

        var error = await Assert.ThrowsAsync<PhotonException>(() => Client.StatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PhotonFailure.Unavailable, error.Failure);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(PhotonClientTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            test._requests.Add(request.RequestUri!);
            return Task.FromResult(test._respond(request));
        }
    }
}
