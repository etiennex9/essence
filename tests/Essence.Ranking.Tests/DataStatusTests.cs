using System.Net;
using System.Text;
using System.Text.Json;

namespace Essence.Ranking.Tests;

public class DataStatusTests
{
    private const string Building = """
        {"service":"routing","version":"OSRM v26.10.0","state":"building","serving":false,"stage":"extracting","percent":45,
         "startedAt":"2026-10-28T02:52:52Z","dataDate":null,"lastError":null,"nextAttemptAt":null,"nextRefreshAt":null,
         "heartbeat":"2026-10-28T02:53:42Z"}
        """;

    [Fact]
    public void A_build_s_status_is_read()
    {
        var status = Parse(Building);

        Assert.Equal(
            new DataStatus("building", false, "extracting", 45, null, null, null, new DateTimeOffset(2026, 10, 28, 2, 53, 42, TimeSpan.Zero)),
            status);
    }

    [Fact]
    public void A_service_waiting_to_try_again_says_why_and_when()
    {
        var status = Parse("""
            {"state":"waiting","serving":true,"stage":null,"percent":null,"dataDate":"2026-10-27",
             "lastError":"the download of quebec-latest.osm.pbf failed","nextAttemptAt":"2026-10-28T03:10:00Z",
             "heartbeat":"2026-10-28T03:05:00Z"}
            """)!;

        Assert.Equal(("waiting", true, "2026-10-27"), (status.State, status.Serving, status.DataDate));
        Assert.Equal("the download of quebec-latest.osm.pbf failed", status.LastError);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 3, 10, 0, TimeSpan.Zero), status.NextAttemptAt);
    }

    [Theory]
    [InlineData("""{"serving":false,"heartbeat":"2026-10-28T02:53:42Z"}""")]
    [InlineData("""{"state":"building","heartbeat":"2026-10-28T02:53:42Z"}""")]
    [InlineData("""{"state":"building","serving":"no","heartbeat":"2026-10-28T02:53:42Z"}""")]
    [InlineData("""{"state":"building","serving":false}""")]
    [InlineData("""{"state":"building","serving":false,"heartbeat":"soon"}""")]
    [InlineData("""[]""")]
    public void Something_that_isn_t_a_status_is_none(string json) =>
        Assert.Null(Parse(json));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(140, 100)]
    public void A_percent_stays_between_0_and_100(int given, int read) =>
        Assert.Equal(read, Parse($$"""{"state":"building","serving":false,"percent":{{given}},"heartbeat":"2026-10-28T02:53:42Z"}""")!.Percent);

    [Fact]
    public void A_status_over_2_minutes_old_is_stale()
    {
        var status = Parse(Building)!;

        Assert.True(status.IsFresh(status.Heartbeat.AddSeconds(119)));
        Assert.False(status.IsFresh(status.Heartbeat.AddMinutes(2)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Not found")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task A_status_that_can_t_be_had_is_none(HttpStatusCode code, string body)
    {
        var client = new DataStatusClient(new HttpClient(new Stub(_ => new HttpResponseMessage(code) { Content = new StringContent(body) }))
        {
            BaseAddress = new Uri("http://routing.test:9090/"),
        });

        Assert.Null(await client.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_client_asks_for_the_status_and_reads_it()
    {
        Uri? asked = null;
        var client = new DataStatusClient(new HttpClient(new Stub(request =>
        {
            asked = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Building, Encoding.UTF8, "application/json") };
        }))
        {
            BaseAddress = new Uri("http://routing.test:9090/"),
        });

        var status = await client.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("http://routing.test:9090/status", asked!.ToString());
        Assert.Equal(45, status!.Percent);
    }

    [Fact]
    public async Task A_server_that_doesn_t_answer_has_no_status()
    {
        var client = new DataStatusClient(new HttpClient(new Stub(_ => throw new HttpRequestException("Connection refused")))
        {
            BaseAddress = new Uri("http://routing.test:9090/"),
        });

        Assert.Null(await client.GetAsync(TestContext.Current.CancellationToken));
    }

    private static DataStatus? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DataStatus.Parse(document.RootElement);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
