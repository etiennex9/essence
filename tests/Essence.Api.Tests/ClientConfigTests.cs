using System.Net;
using System.Net.Http.Json;
using Essence.Contracts;
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
            new(new HoursRow(station, source, rule, true, false, null, null), 45.5, -73.6, reference);

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
            new Dictionary<long, string> { [1] = "node/6756481766", [2] = "way/456", [3] = "relation/789" },
            elements);
        var snapshot = new Snapshot(null, [], null, new Dictionary<long, SnapshotHours>(), null, null, elements);
        Assert.Equal("way/456", snapshot.OsmElementOf(2));
        Assert.Null(snapshot.OsmElementOf(4));
        Assert.Null(new Snapshot(null, [], null, new Dictionary<long, SnapshotHours>(), null, null).OsmElementOf(1));
    }

    [Fact]
    public async Task The_settings_endpoint_is_in_the_API_s_description()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        var document = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"/client-config\"", document);
        Assert.Contains("osmElement", document);
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
