using System.Net;
using System.Text;
using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class PlaceAddressesTests
{
    private const string AddressJson =
        """{"label":"1000 Rue De La Gauchetière Ouest","detail":"Montréal, QC H3A 4W5","latitude":45.49876,"longitude":-73.56657,"kind":"address"}""";

    private static readonly GeoPoint Downtown = new(45.4988, -73.5666);

    private readonly List<Uri> _requests = [];
    private HttpStatusCode _status = HttpStatusCode.OK;

    private PlaceAddresses NewAddresses() =>
        new(new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") }));

    [Fact]
    public async Task A_point_s_address_is_asked_once()
    {
        var addresses = NewAddresses();

        var first = await addresses.LookupAsync(Downtown, "fr", TestContext.Current.CancellationToken);
        var again = await addresses.LookupAsync(new GeoPoint(45.498801, -73.566601), "fr", TestContext.Current.CancellationToken);

        Assert.Equal("1000 Rue De La Gauchetière Ouest", first.Place!.Label);
        Assert.Same(first, again);
        Assert.Equal("/places/reverse?lat=45.4988&lon=-73.5666&lang=fr", Assert.Single(_requests).PathAndQuery);
    }

    [Fact]
    public async Task Outside_Québec_says_so_and_is_kept()
    {
        _status = HttpStatusCode.UnprocessableEntity;
        var addresses = NewAddresses();

        var lookup = await addresses.LookupAsync(new GeoPoint(45.426, -75.695), "en", TestContext.Current.CancellationToken);
        await addresses.LookupAsync(new GeoPoint(45.426, -75.695), "en", TestContext.Current.CancellationToken);

        Assert.True(lookup.OutsideQuebec);
        Assert.Null(lookup.Place);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Nothing_near_is_an_answer_too()
    {
        _status = HttpStatusCode.NotFound;
        var addresses = NewAddresses();

        var lookup = await addresses.LookupAsync(Downtown, "fr", TestContext.Current.CancellationToken);
        await addresses.LookupAsync(Downtown, "fr", TestContext.Current.CancellationToken);

        Assert.Equal((null, false, ApiError.NotFound), (lookup.Place, lookup.OutsideQuebec, lookup.Error));
        Assert.Single(_requests);
    }

    [Fact]
    public async Task A_failure_isn_t_kept_so_the_next_look_asks_again()
    {
        _status = HttpStatusCode.ServiceUnavailable;
        var addresses = NewAddresses();

        var lookup = await addresses.LookupAsync(Downtown, "fr", TestContext.Current.CancellationToken);
        _status = HttpStatusCode.OK;
        var again = await addresses.LookupAsync(Downtown, "fr", TestContext.Current.CancellationToken);

        Assert.Equal(ApiError.Unavailable, lookup.Error);
        Assert.NotNull(again.Place);
        Assert.Equal(2, _requests.Count);
    }

    [Theory]
    [InlineData("1000 Rue De La Gauchetière Ouest", "Montréal, QC H3A 4W5", "address", "1000 Rue De La Gauchetière Ouest, Montréal")]
    [InlineData("1000 Rue Principale", "", "address", "1000 Rue Principale")]
    [InlineData("Rue Gaucher", "Marieville", "street", "Rue Gaucher, Marieville")]
    [InlineData("Cégep de Sherbrooke", "475 Rue du Cégep, Sherbrooke", "named", "Cégep de Sherbrooke")]
    [InlineData("Laval", "Laval", "town", "Laval")]
    public void A_short_name_is_the_label_and_the_town_of_an_address_or_a_street(string label, string detail, string kind, string shortName) =>
        Assert.Equal(shortName, PlaceAddresses.ShortName(new PlaceSuggestion(label, detail, 45.5, -73.6, kind)));

    private sealed class StubHandler(PlaceAddressesTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(test._status)
            {
                Content = new StringContent(test._status == HttpStatusCode.OK ? AddressJson : "{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
