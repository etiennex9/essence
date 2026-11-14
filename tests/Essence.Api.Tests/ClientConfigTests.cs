using System.Net;
using System.Net.Http.Json;
using Essence.Contracts;
using Essence.Data.Entities;
using Essence.Ranking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Essence.Api.Tests;

/// <summary>
/// <c>/client-config</c>, which names the deployment's OpenStreetMap application to the apps (the Android app picks its server
/// at run time), and the OpenStreetMap element a station's details carry, so a correction of its hours can go there.
/// </summary>
public sealed class ClientConfigTests
{
    [Fact]
    public async Task Without_a_client_id_editing_OpenStreetMap_is_off_on_OpenStreetMap_s_own_servers()
    {
        using var factory = Factory();

        var config = await factory.CreateClient().GetFromJsonAsync<ClientConfig>("/client-config", TestContext.Current.CancellationToken);

        Assert.Equal(new ClientConfig(null, "https://www.openstreetmap.org", "https://api.openstreetmap.org"), config);
    }

    [Fact]
    public async Task The_client_id_and_the_servers_come_from_the_settings_without_a_trailing_slash()
    {
        using var factory = Factory(
            ("Osm:ClientId", " client-abc "),
            ("Osm:AuthUrl", "https://master.apis.dev.openstreetmap.org/"),
            ("Osm:ApiUrl", "https://master.apis.dev.openstreetmap.org"));

        var response = await factory.CreateClient().GetAsync("/client-config", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"osmClientId":"client-abc","osmAuthUrl":"https://master.apis.dev.openstreetmap.org","osmApiUrl":"https://master.apis.dev.openstreetmap.org"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_address_that_isn_t_one_stops_the_API_at_its_start()
    {
        using var factory = Factory(("Osm:ClientId", "client-abc"), ("Osm:AuthUrl", "www.openstreetmap.org"));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Osm:AuthUrl", error.Message);
    }

    [Theory]
    [InlineData("http://osm.example", "http://osm.example", "Osm:AuthUrl")]
    [InlineData("https://www.openstreetmap.org", "http://api.openstreetmap.org", "Osm:ApiUrl")]
    [InlineData("https://master.apis.dev.openstreetmap.org", "", "Osm:ApiUrl")]
    [InlineData("", "https://master.apis.dev.openstreetmap.org", "Osm:ApiUrl")]
    [InlineData("https://www.openstreetmap.org", "https://osm.example", "Osm:ApiUrl")]
    public void Servers_the_apps_wouldn_t_send_a_token_to_stop_the_API_at_its_start(string authUrl, string apiUrl, string named)
    {
        using var factory = Factory(("Osm:ClientId", "client-abc"), ("Osm:AuthUrl", authUrl), ("Osm:ApiUrl", apiUrl));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(named, error.Message);
    }

    [Fact]
    public async Task An_OpenStreetMap_on_this_machine_may_be_plain_http()
    {
        using var factory = Factory(("Osm:ClientId", "local"), ("Osm:AuthUrl", "http://localhost:3000"), ("Osm:ApiUrl", "http://localhost:3000/"));

        var config = await factory.CreateClient().GetFromJsonAsync<ClientConfig>("/client-config", TestContext.Current.CancellationToken);

        Assert.Equal(new ClientConfig("local", "http://localhost:3000", "http://localhost:3000"), config);
    }

    [Fact]
    public void A_station_s_element_is_its_OpenStreetMap_row_s_even_without_hours_and_only_an_element()
    {
        SnapshotHoursRow Row(long station, string source, string? rule, string reference) =>
            new(new HoursRow(station, source, rule, true, false, null, null), 45.5, -73.6, reference, StationHoursEntry.ByDistance);

        var elements = StationSnapshot.OsmElementsOf(
        [
            Row(1, "osm", "Mo-Su 07:00-21:00", "node/6756481766"),
            Row(2, "osm", null, "way/456"),
            Row(3, "osm", null, "relation/789"),
            Row(4, "costco", "Mo-Fr 06:00-21:30", "1182"),
            Row(5, "osm", null, "node/0"),
            Row(6, "osm", null, "https://www.openstreetmap.org/node/1"),
            Row(7, "osm", null, "area/12"),
        ]);

        Assert.Equal(
            new Dictionary<long, SnapshotOsmElement>
            {
                [1] = new("node/6756481766", BrandDiffers: false),
                [2] = new("way/456", BrandDiffers: false),
                [3] = new("relation/789", BrandDiffers: false),
            },
            elements);
        var snapshot = new Snapshot(null, [], null, new Dictionary<long, SnapshotHours>(), null, null, elements);
        Assert.Equal("way/456", snapshot.OsmElementOf(2)?.Ref);
        Assert.Null(snapshot.OsmElementOf(4));
        Assert.Null(new Snapshot(null, [], null, new Dictionary<long, SnapshotHours>(), null, null).OsmElementOf(1));
    }

    [Theory]
    [InlineData(StationHoursEntry.ByLocation, true)]
    [InlineData(StationHoursEntry.ByDistance, false)]
    [InlineData(StationHoursEntry.ByAddress, false)]
    [InlineData(StationHoursEntry.ByBrand, false)]
    [InlineData(null, false)]
    public void Only_an_element_matched_by_place_alone_has_another_brand_or_none(string? matchedBy, bool brandDiffers)
    {
        // A station on an element of another brand, or none: matched by location, its hours not taken, its brand likely old.
        var row = new SnapshotHoursRow(new HoursRow(1, "osm", null, true, false, null, null), 48.42, -71.07, "way/456", matchedBy);

        var element = StationSnapshot.OsmElementsOf([row])[1];

        Assert.Equal(new SnapshotOsmElement("way/456", brandDiffers), element);
    }

    [Theory]
    [InlineData("Sonic", "Sonic")]
    [InlineData("Petroles RL", "Petroles RL")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void The_element_s_brand_is_as_the_last_run_read_it_and_blank_is_none(string? read, string? brand)
    {
        // Nutrinor's station on an element named "Sonic", without a brand tag: named after its old brand.
        var row = new SnapshotHoursRow(
            new HoursRow(1, "osm", null, false, false, null, null), 48.42, -71.07, "node/5310653589", StationHoursEntry.ByLocation, read);

        Assert.Equal(new SnapshotOsmElement("node/5310653589", BrandDiffers: true, brand), StationSnapshot.OsmElementsOf([row])[1]);
    }

    [Fact]
    public async Task The_settings_endpoint_is_in_the_API_s_description()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        var document = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"/client-config\"", document);
        Assert.Contains("osmElement", document);
        Assert.Contains("osmBrandDiffers", document);
        Assert.Contains("\"osmBrand\"", document);
        // Explained where the station is described, as the XML docs don't reach the document.
        Assert.Contains("`osmBrandDiffers` is true when that element was matched by place only", document);
        Assert.Contains("`osmBrand` is the element", document);
    }

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Essence", "Host=localhost;Database=unused");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                foreach (var service in (DataService[])[DataService.Routing, DataService.Geocoding])
                {
                    services.AddHttpClient(DataServiceMonitor.ClientName(service))
                        .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
                }
            });
        });

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
