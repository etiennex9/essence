using System.Net;
using System.Text.Json;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Essence.Api.Tests;

/// <summary>
/// The hours sources' status and health, read from what <c>hours_imports</c> says. Reading the table needs PostgreSQL,
/// which these tests don't have: the status and the check's verdict are built from rows given here, and the endpoint is
/// only checked to be in the API's description.
/// </summary>
public sealed class HoursSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(21);

    private static HoursImport Run(long id, double daysAgo, HoursImportStatus status, string? error = null) => new()
    {
        Id = id,
        Source = "costco",
        StartedAt = Now.AddDays(-daysAgo),
        FinishedAt = Now.AddDays(-daysAgo).AddSeconds(2),
        Status = status,
        SourceVersion = $"v{id}",
        Error = error,
    };

    [Fact]
    public void A_source_that_failed_says_why_in_one_line_how_many_times_in_a_row_and_the_version_still_serving()
    {
        var history = HoursHistory.Summarize("costco",
        [
            Run(1, 8, HoursImportStatus.Succeeded),
            Run(2, 1, HoursImportStatus.Rejected, "0 stations matched, fewer than 80 % of the last good run's 18."),
            Run(3, 0.9, HoursImportStatus.Failed, "System.Net.Http.HttpRequestException: 403 (Forbidden).\n   at Essence.Ingest…"),
        ]);

        var status = HoursSources.ToStatus(history, stations: 18);

        Assert.Equal(
            new HoursSourceStatus(
                "costco", Now.AddDays(-8).AddSeconds(2), Now.AddDays(-0.9).AddSeconds(2), "failed",
                "System.Net.Http.HttpRequestException: 403 (Forbidden).", 2, 18, "v1"),
            status);
    }

    [Fact]
    public void A_source_that_succeeded_has_no_error_and_its_last_status()
    {
        var status = HoursSources.ToStatus(
            HoursHistory.Summarize("costco", [Run(1, 8, HoursImportStatus.Failed, "boom"), Run(2, 7, HoursImportStatus.Unchanged)]),
            stations: 18);

        Assert.Equal(("unchanged", null, 0), (status.LastStatus, status.LastError, status.ConsecutiveFailures));
    }

    [Fact]
    public void Hours_fetched_within_21_days_are_healthy()
    {
        var result = HoursHealthCheck.Evaluate([Status("costco", Now.AddDays(-7)), Status("curated", Now)], Now, StaleAfter, HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(Now.AddDays(-7), result.Data["costco"]);
        Assert.Equal(21.0, result.Data["staleAfterDays"]);
    }

    [Fact]
    public void A_source_without_a_success_in_21_days_makes_the_hours_degraded_and_is_named()
    {
        var result = HoursHealthCheck.Evaluate([Status("costco", Now.AddDays(-22)), Status("curated", Now)], Now, StaleAfter, HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("costco", result.Description);
        Assert.DoesNotContain("curated", result.Description);
    }

    [Fact]
    public void A_source_that_ran_but_never_succeeded_is_degraded_too_and_one_that_never_ran_doesn_t_count()
    {
        var never = HoursHealthCheck.Evaluate([Status("costco", null)], Now, StaleAfter, HealthStatus.Degraded);
        var none = HoursHealthCheck.Evaluate([], Now, StaleAfter, HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Degraded, never.Status);
        Assert.Equal("No success yet for the hours from costco.", never.Description);
        Assert.Equal("never", never.Data["costco"]);
        Assert.Equal(HealthStatus.Healthy, none.Status);
    }

    [Fact]
    public void Stale_sources_and_sources_that_never_succeeded_are_both_named()
    {
        var result = HoursHealthCheck.Evaluate(
            [Status("costco", Now.AddDays(-22)), Status("curated", Now), Status("shell", null)], Now, StaleAfter, HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(
            "No success within 21 days for the hours from costco: they're stale. No success yet for the hours from shell.",
            result.Description);
    }

    [Fact]
    public async Task The_sources_endpoint_is_in_the_API_s_description()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Essence", "Host=localhost;Database=unused");
            builder.ConfigureTestServices(services =>
            {
                foreach (var service in (DataService[])[DataService.Routing, DataService.Geocoding])
                {
                    services.AddHttpClient(DataServiceMonitor.ClientName(service))
                        .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
                }
            });
        });

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var sources = document.RootElement.GetProperty("paths").GetProperty("/hours/sources").GetProperty("get");
        Assert.Equal("Hours", sources.GetProperty("tags")[0].GetString());
    }

    private static HoursSourceStatus Status(string source, DateTimeOffset? lastSuccessAt) =>
        new(source, lastSuccessAt, lastSuccessAt, lastSuccessAt is null ? "failed" : "succeeded", null, 0, 18, "v1");

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
