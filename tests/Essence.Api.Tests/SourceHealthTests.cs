using System.Net;
using System.Text.Json;
using Essence.Api.Endpoints;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Api.Tests;

/// <summary>
/// <c>/health/sources</c>, which a monitor alerts on: the feed and each hours source, judged from their runs. Reading
/// <c>imports</c> and <c>hours_imports</c> needs PostgreSQL, which these tests don't have: the verdicts are built from runs
/// given here, read the way <see cref="HoursHistory"/> reads them, and the endpoint itself is checked with its database
/// unreachable, its answer's shape, and its place in the API's description.
/// </summary>
public sealed class SourceHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly SourceHealthSettings Settings = SourceHealthSettings.Default;

    /// <summary>A recent feed, so that only the hours sources decide.</summary>
    private static readonly FeedRuns FreshFeed = new(Now.AddMinutes(-4), Now.AddMinutes(-4), 0, null);

    private static HoursImport Run(long id, TimeSpan ago, HoursImportStatus status, string? error = null, string source = "costco") => new()
    {
        Id = id,
        Source = source,
        StartedAt = Now - ago,
        FinishedAt = Now - ago + TimeSpan.FromSeconds(2),
        Status = status,
        SourceVersion = status is HoursImportStatus.Failed ? null : $"v{id}",
        Error = error,
    };

    private static TimeSpan Days(double days) => TimeSpan.FromDays(days);

    private static TimeSpan Hours(double hours) => TimeSpan.FromHours(hours);

    private static IngestSourceHealth Costco(params HoursImport[] runs) =>
        SourceHealth.ForHours(HoursHistory.Summarize("costco", runs), Now, Settings);

    [Fact]
    public void Two_failures_in_a_row_are_fine_a_retry_is_on_its_way()
    {
        var costco = Costco(
            Run(1, Days(7), HoursImportStatus.Succeeded),
            Run(2, Hours(1.3), HoursImportStatus.Failed, "System.Net.Http.HttpRequestException: 503 (Service Unavailable)."),
            Run(3, Hours(1), HoursImportStatus.Failed, "System.Net.Http.HttpRequestException: 503 (Service Unavailable)."));

        Assert.True(costco.Ok);
        Assert.Null(costco.Reason);
        Assert.Equal(2, costco.ConsecutiveFailures);
        Assert.Equal("System.Net.Http.HttpRequestException: 503 (Service Unavailable).", costco.LastError);
    }

    [Fact]
    public void Three_failures_in_a_row_fail_the_source_with_its_last_error_and_the_whole_report()
    {
        var history = HoursHistory.Summarize("costco",
        [
            Run(1, Days(7), HoursImportStatus.Succeeded),
            Run(2, Hours(1.3), HoursImportStatus.Failed, "System.Net.Http.HttpRequestException: 503 (Service Unavailable)."),
            Run(3, Hours(1), HoursImportStatus.Failed, "System.Net.Http.HttpRequestException: 503 (Service Unavailable)."),
            Run(4, TimeSpan.Zero, HoursImportStatus.Failed, "System.Net.Http.HttpRequestException: 403 (Forbidden).\n   at Essence.Ingest…"),
        ]);
        var curated = HoursHistory.Summarize("curated", [Run(5, Days(1), HoursImportStatus.Unchanged, source: "curated")]);

        var report = SourceHealth.Report(Now, Settings, FreshFeed, [history, curated]);

        Assert.Equal("failing", report.Status);
        Assert.Equal(new[] { "feed", "costco", "curated" }, report.Sources.Select(s => s.Source));
        Assert.Equal(
            new IngestSourceHealth(
                "costco", false, "failing", 3, "System.Net.Http.HttpRequestException: 403 (Forbidden).",
                Now - Days(7) + TimeSpan.FromSeconds(2), Now + TimeSpan.FromSeconds(2)),
            report.Sources[1]);
        Assert.True(report.Sources[0].Ok);
        Assert.True(report.Sources[2].Ok);
    }

    [Theory]
    [InlineData(HoursImportStatus.Succeeded)]
    [InlineData(HoursImportStatus.Unchanged)]
    public void The_next_success_clears_it_changed_or_not(HoursImportStatus success)
    {
        var costco = Costco(
            Run(1, Days(7), HoursImportStatus.Succeeded),
            Run(2, Hours(8), HoursImportStatus.Failed, "boom"),
            Run(3, Hours(7), HoursImportStatus.Failed, "boom"),
            Run(4, Hours(6), HoursImportStatus.Failed, "boom"),
            Run(5, TimeSpan.Zero, success));

        Assert.Equal((true, null, 0, null), (costco.Ok, costco.Reason, costco.ConsecutiveFailures, costco.LastError));
        Assert.Equal(Now + TimeSpan.FromSeconds(2), costco.LastSuccessAt);
    }

    [Fact]
    public void A_rejected_run_counts_like_a_failed_one()
    {
        var costco = Costco(
            Run(1, Days(7), HoursImportStatus.Succeeded),
            Run(2, Hours(1.3), HoursImportStatus.Failed, "boom"),
            Run(3, Hours(1), HoursImportStatus.Failed, "boom"),
            Run(4, TimeSpan.Zero, HoursImportStatus.Rejected, "0 stations matched, fewer than 80 % of the last good run's 18."));

        Assert.Equal((false, "failing", 3), (costco.Ok, costco.Reason, costco.ConsecutiveFailures));
        Assert.Equal("0 stations matched, fewer than 80 % of the last good run's 18.", costco.LastError);
    }

    [Fact]
    public void A_run_matched_again_without_fetching_neither_counts_nor_clears()
    {
        HoursImport[] twoAndRematches =
        [
            Run(1, Days(7), HoursImportStatus.Succeeded),
            Run(2, Hours(3), HoursImportStatus.Failed, "boom"),
            Run(3, Hours(2.5), HoursImportStatus.Rematched),
            Run(4, Hours(2), HoursImportStatus.Failed, "boom"),
            Run(5, Hours(1.5), HoursImportStatus.Rematched),
        ];

        var two = Costco(twoAndRematches);
        var three = Costco([.. twoAndRematches, Run(6, Hours(1), HoursImportStatus.Failed, "boom"), Run(7, TimeSpan.Zero, HoursImportStatus.Rematched)]);

        Assert.Equal((true, 2), (two.Ok, two.ConsecutiveFailures));
        Assert.Equal((false, "failing", 3, "boom"), (three.Ok, three.Reason, three.ConsecutiveFailures, three.LastError));
        Assert.Equal(Now - Hours(1) + TimeSpan.FromSeconds(2), three.LastAttemptAt);
    }

    [Fact]
    public void A_source_without_a_success_in_21_days_is_stale_even_without_failures()
    {
        var history = HoursHistory.Summarize("costco", [Run(1, Days(1), HoursImportStatus.Succeeded)]);
        var clock = new FakeTimeProvider(history.LastSuccess!.FinishedAt + Days(21));

        var onTheDay = SourceHealth.ForHours(history, clock.GetUtcNow(), Settings);
        clock.Advance(TimeSpan.FromMinutes(1));
        var after = SourceHealth.ForHours(history, clock.GetUtcNow(), Settings);

        Assert.True(onTheDay.Ok);
        Assert.Equal((false, "stale", 0, null), (after.Ok, after.Reason, after.ConsecutiveFailures, after.LastError));
    }

    [Fact]
    public void A_source_failing_that_long_is_failing_rather_than_stale()
    {
        var costco = Costco(
            Run(1, Days(30), HoursImportStatus.Succeeded),
            Run(2, Days(23), HoursImportStatus.Failed, "boom"),
            Run(3, Days(22), HoursImportStatus.Failed, "boom"),
            Run(4, Days(21), HoursImportStatus.Failed, "boom"));

        Assert.Equal("failing", costco.Reason);
    }

    [Fact]
    public void A_source_that_ran_but_never_succeeded_fails_once_it_has_tried_3_times()
    {
        var twice = Costco(Run(1, Hours(1.3), HoursImportStatus.Failed, "boom"), Run(2, Hours(1), HoursImportStatus.Rejected, "nothing"));
        var thrice = Costco(
            Run(1, Hours(1.3), HoursImportStatus.Failed, "boom"),
            Run(2, Hours(1), HoursImportStatus.Rejected, "nothing"),
            Run(3, TimeSpan.Zero, HoursImportStatus.Failed, "boom again"));

        Assert.Equal((true, null, 2), (twice.Ok, twice.Reason, twice.ConsecutiveFailures));
        Assert.Equal((false, "never succeeded", 3, "boom again"), (thrice.Ok, thrice.Reason, thrice.ConsecutiveFailures, thrice.LastError));
        Assert.Null(thrice.LastSuccessAt);
    }

    [Fact]
    public void A_source_that_never_succeeded_and_wasn_t_tried_again_in_21_days_is_stale()
    {
        var costco = Costco(Run(1, Days(22), HoursImportStatus.Failed, "boom"));

        Assert.Equal((false, "stale", 1), (costco.Ok, costco.Reason, costco.ConsecutiveFailures));
    }

    [Fact]
    public void A_source_turned_off_isn_t_listed()
    {
        var shell = HoursHistory.Summarize("shell",
        [
            Run(1, Days(30), HoursImportStatus.Failed, "boom", "shell"),
            Run(2, Days(29), HoursImportStatus.Failed, "boom", "shell"),
            Run(3, Days(28), HoursImportStatus.Failed, "boom", "shell"),
            Run(4, Days(1), HoursImportStatus.Disabled, source: "shell"),
        ]);
        var costco = HoursHistory.Summarize("costco", [Run(5, Days(2), HoursImportStatus.Succeeded)]);

        var report = SourceHealth.Report(Now, Settings, FreshFeed, [shell, costco]);

        Assert.Equal("ok", report.Status);
        Assert.Equal(new[] { "feed", "costco" }, report.Sources.Select(s => s.Source));
    }

    [Fact]
    public void The_feed_fails_without_a_successful_import_in_15_minutes()
    {
        var clock = new FakeTimeProvider(Now);
        var feed = new FeedRuns(Now, Now.AddMinutes(10), 2, "Npgsql.PostgresException: 23505: duplicate key value…\n   at Essence.Ingest…");

        clock.Advance(TimeSpan.FromMinutes(15));
        var onTime = SourceHealth.ForFeed(feed, clock.GetUtcNow(), Settings);
        clock.Advance(TimeSpan.FromSeconds(1));
        var late = SourceHealth.ForFeed(feed, clock.GetUtcNow(), Settings);
        var report = SourceHealth.Report(clock.GetUtcNow(), Settings, feed, []);

        Assert.True(onTime.Ok);
        Assert.Equal(
            new IngestSourceHealth("feed", false, "feed stale", 2, "Npgsql.PostgresException: 23505: duplicate key value…", Now, Now.AddMinutes(10)),
            late);
        Assert.Equal(("failing", 1), (report.Status, report.Sources.Count));
    }

    [Fact]
    public void A_feed_never_imported_has_never_succeeded()
    {
        var feed = SourceHealth.ForFeed(new FeedRuns(null, null, 0, null), Now, Settings);

        Assert.Equal((false, "never succeeded"), (feed.Ok, feed.Reason));
    }

    [Fact]
    public void Every_source_fine_is_ok_and_routing_and_geocoding_aren_t_sources()
    {
        var costco = HoursHistory.Summarize("costco", [Run(1, Days(6), HoursImportStatus.Succeeded), Run(2, Hours(1), HoursImportStatus.Failed, "boom")]);
        var osm = HoursHistory.Summarize("osm", [Run(3, Days(3), HoursImportStatus.Unchanged, source: "osm")]);

        var report = SourceHealth.Report(Now, Settings, FreshFeed, [osm, costco]);

        Assert.Equal(new SourceHealthReport("ok", Now, report.Sources, null), report);
        Assert.Equal(new[] { "feed", "costco", "osm" }, report.Sources.Select(s => s.Source));
        Assert.All(report.Sources, s => Assert.True(s.Ok));
    }

    [Fact]
    public void The_settings_come_from_Health_and_at_least_one_failure_alerts()
    {
        static SourceHealthSettings Read(params (string Key, string Value)[] settings) => SourceHealthSettings.From(
            new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build());

        Assert.Equal(SourceHealthSettings.Default, Read());
        Assert.Equal(
            new SourceHealthSettings(TimeSpan.FromMinutes(30), Days(14), 5),
            Read(("Health:MaxImportAgeMinutes", "30"), ("Health:HoursStaleAfterDays", "14"), ("Health:HoursAlertAfterFailures", "5")));
        Assert.Equal(1, Read(("Health:HoursAlertAfterFailures", "0")).AlertAfterFailures);
    }

    [Fact]
    public void An_ok_report_is_a_200_and_any_other_a_503_with_the_report_or_for_HEAD_only_the_status()
    {
        var ok = new SourceHealthReport("ok", Now, []);
        var failing = SourceHealth.Unreachable(Now);

        Assert.Same(ok, Assert.IsType<Ok<SourceHealthReport>>(SourceHealthEndpoints.Respond(ok).Result).Value);
        var json = Assert.IsType<JsonHttpResult<SourceHealthReport>>(SourceHealthEndpoints.Respond(failing).Result);
        Assert.Equal(503, json.StatusCode);
        Assert.Same(failing, json.Value);
        Assert.Equal(200, Assert.IsType<StatusCodeHttpResult>(SourceHealthEndpoints.Respond(ok, head: true).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<StatusCodeHttpResult>(SourceHealthEndpoints.Respond(failing, head: true).Result).StatusCode);
    }

    [Fact]
    public void The_answer_names_each_source_s_state_in_the_API_s_JSON()
    {
        using var factory = Factory();
        var json = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var report = SourceHealth.Report(
            Now,
            Settings,
            new FeedRuns(Now.AddMinutes(-20), Now.AddMinutes(-20), 0, null),
            [HoursHistory.Summarize("costco", [Run(1, Days(30), HoursImportStatus.Succeeded)])]);

        Assert.Equal(
            """{"status":"failing","checkedAt":"2026-11-13T09:00:00+00:00","sources":["""
            + """{"source":"feed","ok":false,"reason":"feed stale","consecutiveFailures":0,"lastError":null,"lastSuccessAt":"2026-11-13T08:40:00+00:00","lastAttemptAt":"2026-11-13T08:40:00+00:00"},"""
            + """{"source":"costco","ok":false,"reason":"stale","consecutiveFailures":0,"lastError":null,"lastSuccessAt":"2026-10-14T09:00:02+00:00","lastAttemptAt":"2026-10-14T09:00:02+00:00"}"""
            + """],"reason":null}""",
            JsonSerializer.Serialize(report, json));
    }

    [Fact]
    public async Task A_database_that_can_t_be_read_is_a_503_that_says_so_and_nothing_more()
    {
        var clock = new FakeTimeProvider(Now);
        using var factory = Factory(clock);

        var response = await factory.CreateClient().GetAsync("/health/sources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("""{"status":"failing","checkedAt":"2026-11-13T09:00:00+00:00","sources":[],"reason":"database unreachable"}""", body);
    }

    [Fact]
    public async Task HEAD_answers_the_status_alone_and_neither_is_rate_limited()
    {
        using var factory = Factory(null, ("RateLimiting:PermitLimit", "1"));
        var client = factory.CreateClient();

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health/sources"), TestContext.Current.CancellationToken);
        using var again = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health/sources"), TestContext.Current.CancellationToken);
        using var get = await client.GetAsync("/health/sources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, again.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_is_in_the_API_s_description_for_GET_and_HEAD()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var path = document.RootElement.GetProperty("paths").GetProperty("/health/sources");
        var get = path.GetProperty("get");
        Assert.Equal("Health", get.GetProperty("tags")[0].GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(get.GetProperty("responses").TryGetProperty("503", out _));
        Assert.True(path.TryGetProperty("head", out _));
    }

    /// <summary>
    /// The API with a database that isn't there: 127.0.0.1's port 1, given up on after a second, so that every read of it
    /// fails at once.
    /// </summary>
    private static WebApplicationFactory<Program> Factory(TimeProvider? clock = null, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Essence", "Host=127.0.0.1;Port=1;Database=unused;Timeout=1");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                if (clock is not null)
                {
                    services.AddSingleton(clock);
                }

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
