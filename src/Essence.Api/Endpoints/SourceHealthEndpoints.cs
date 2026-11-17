using Essence.Contracts;
using Essence.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api.Endpoints;

/// <summary>
/// <c>/health/sources</c>, for a monitor such as Uptime Kuma (README → Alerts). Not rate limited, as <c>/health</c> isn't:
/// a monitor sharing an address with other clients must never read a 429 as the sources failing.
/// </summary>
public static class SourceHealthEndpoints
{
    public static IEndpointRouteBuilder MapSourceHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/health/sources", [HttpMethods.Get, HttpMethods.Head], Check)
            .WithTags("Health")
            .WithSummary("Whether every source the ingest reads is doing fine, for a monitor to alert on")
            .WithDescription(
                "200 when every source is fine, 503 when one isn't (`status` `failing`), and HEAD answers the same status "
                + "without the body. The Régie's feed (`feed`) fails when its last successful import is older than "
                + "`Health:MaxImportAgeMinutes` (15 min, about 3 of its versions): `feed stale`, or `never succeeded`. Each "
                + "hours source that has run and isn't turned off fails after `Health:HoursAlertAfterFailures` (3) failed or "
                + "rejected fetches in a row (`failing`, or `never succeeded` when it never had a success), or without a "
                + "success in `Health:HoursStaleAfterDays` (21) days (`stale`); a run matched again without fetching neither "
                + "counts nor clears, and the next success clears it. Each source says how many times in a row it failed, its "
                + "last error and when it last succeeded and was tried. Routing and geocoding don't count, so building their "
                + "data never fails it. When the database can't be read: 503, `reason` `database unreachable`, no sources.")
            .Produces<SourceHealthReport>(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<Results<Ok<SourceHealthReport>, JsonHttpResult<SourceHealthReport>, StatusCodeHttpResult>> Check(
        EssenceDbContext db,
        TimeProvider time,
        IConfiguration configuration,
        ILoggerFactory loggers,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var now = time.GetUtcNow();
        SourceHealthReport report;
        try
        {
            report = await SourceHealth.LoadAsync(db, now, SourceHealthSettings.From(configuration), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The error stays in the logs: the answer only says the database can't be read.
            loggers.CreateLogger(SourceHealth.LogCategory).LogWarning(ex, "The ingest's runs can't be read.");
            report = SourceHealth.Unreachable(now);
        }

        var head = HttpMethods.IsHead(http.Request.Method);
        if (head)
        {
            // Only the status: without this, a 503 would get a problem's body from the status code pages.
            http.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;
        }

        return Respond(report, head);
    }

    /// <summary>200 with the report when it's <c>ok</c>, 503 with it otherwise; only the status for a HEAD request.</summary>
    public static Results<Ok<SourceHealthReport>, JsonHttpResult<SourceHealthReport>, StatusCodeHttpResult> Respond(
        SourceHealthReport report, bool head = false)
    {
        var status = report.Status == SourceHealth.OkStatus ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        if (head)
        {
            return TypedResults.StatusCode(status);
        }

        return status == StatusCodes.Status200OK ? TypedResults.Ok(report) : TypedResults.Json(report, statusCode: status);
    }
}
