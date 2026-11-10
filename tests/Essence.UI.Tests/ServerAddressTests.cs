using System.Net;
using Essence.UI.App;

namespace Essence.UI.Tests;

public class ServerAddressTests
{
    private const string ServicesJson = """
        {"routing":{"state":"serving","ready":true},"geocoding":{"state":"building","ready":false,"stage":"downloading","percent":12}}
        """;

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  Example.com  ", "https://example.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com:8443/essence", "https://example.com:8443/essence/")]
    [InlineData("https://example.com/essence/?x=1#top", "https://example.com/essence/")]
    [InlineData("https://example.com:443/", "https://example.com/")]
    public void A_typed_address_becomes_an_https_root(string typed, string expected)
    {
        var result = ServerAddress.Normalize(typed, allowHttp: false);

        Assert.Equal(ServerAddressError.None, result.Error);
        Assert.Equal(expected, result.Root!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null, ServerAddressError.Empty)]
    [InlineData("   ", ServerAddressError.Empty)]
    [InlineData("ftp://example.com", ServerAddressError.Invalid)]
    [InlineData("https://", ServerAddressError.Invalid)]
    [InlineData("https://user:pass@example.com", ServerAddressError.Invalid)]
    [InlineData("exa mple.com", ServerAddressError.Invalid)]
    [InlineData("http://example.com", ServerAddressError.HttpsRequired)]
    public void Addresses_that_can_t_be_a_server_are_refused(string? typed, ServerAddressError error)
    {
        var result = ServerAddress.Normalize(typed, allowHttp: false);

        Assert.Equal(error, result.Error);
        Assert.Null(result.Root);
    }

    [Fact]
    public void Development_builds_also_take_http()
    {
        var result = ServerAddress.Normalize("http://localhost:8080", allowHttp: true);

        Assert.Equal("http://localhost:8080/", result.Root!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://example.com:8443/", "example.com:8443")]
    [InlineData("http://localhost:8080/", "localhost:8080")]
    public void The_server_is_shown_by_its_host_and_any_other_port(string root, string shown) =>
        Assert.Equal(shown, ServerAddress.DisplayName(new Uri(root)));

    [Fact]
    public async Task A_website_s_api_is_found_under_api()
    {
        var http = new TestHttp(r => r.RequestUri!.AbsolutePath == "/api/services" ? TestHttp.Json(ServicesJson) : TestHttp.Status(HttpStatusCode.NotFound));

        var probe = await ServerAddress.ProbeAsync(new Uri("https://example.com/"), new HttpClient(http), TestContext.Current.CancellationToken);

        Assert.Equal(new ServerProbe(new Uri("https://example.com/api/"), ServerProbeError.None), probe);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task An_api_s_own_root_works_too()
    {
        var http = new TestHttp(r => r.RequestUri!.AbsolutePath == "/services" ? TestHttp.Json(ServicesJson) : TestHttp.Status(HttpStatusCode.NotFound));

        var probe = await ServerAddress.ProbeAsync(new Uri("http://localhost:8080/"), new HttpClient(http), TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("http://localhost:8080/"), probe.ApiBase);
        Assert.Equal(["/api/services", "/services"], http.Requests.Select(r => r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task Being_asked_to_slow_down_still_counts_as_an_essence_server()
    {
        var http = new TestHttp(_ => TestHttp.Status(HttpStatusCode.TooManyRequests));

        var probe = await ServerAddress.ProbeAsync(new Uri("https://example.com/"), new HttpClient(http), TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://example.com/api/"), probe.ApiBase);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>")]
    [InlineData("{}")]
    [InlineData("""{"status":"ok"}""")]
    public async Task Another_kind_of_server_is_not_taken(string body)
    {
        var http = new TestHttp(_ => TestHttp.Json(body));

        var probe = await ServerAddress.ProbeAsync(new Uri("https://example.com/"), new HttpClient(http), TestContext.Current.CancellationToken);

        Assert.Equal(new ServerProbe(null, ServerProbeError.NotEssence), probe);
    }

    [Fact]
    public async Task A_server_that_no_longer_serves_this_app_says_so()
    {
        var http = new TestHttp(_ => TestHttp.Status(HttpStatusCode.UpgradeRequired));

        var probe = await ServerAddress.ProbeAsync(new Uri("https://example.com/"), new HttpClient(http), TestContext.Current.CancellationToken);

        Assert.Equal(new ServerProbe(null, ServerProbeError.AppTooOld), probe);
    }

    [Fact]
    public async Task No_answer_at_all_is_unreachable()
    {
        var http = new TestHttp(_ => throw new HttpRequestException("No such host"));

        var probe = await ServerAddress.ProbeAsync(new Uri("https://example.com/"), new HttpClient(http), TestContext.Current.CancellationToken);

        Assert.Equal(new ServerProbe(null, ServerProbeError.Unreachable), probe);
    }
}
