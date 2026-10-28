using System.Collections.Concurrent;
using Essence.Contracts;
using Essence.Ranking;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api;

/// <summary>The containers whose data is built on the host: routing (OSRM) and geocoding (Photon).</summary>
public enum DataService
{
    Routing,
    Geocoding,
}

/// <summary>
/// What the routing and geocoding containers say they're doing (their supervisors' GET :9090/status), each asked at most
/// every 5 seconds. A service that says it isn't serving (its data is being built, or loaded) isn't called: Best near
/// you estimates, trips and the address search answer 503 with its progress, and the routing client's circuit breaker
/// isn't tripped by a build. A status that can't be had (the container is down, or an older image) or is stale leaves
/// things as before: the service is called, and its failure counts.
/// </summary>
public sealed class DataServiceMonitor(IHttpClientFactory clients, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<DataService, (DateTimeOffset At, DataStatus? Status)> _cache = new();

    public static string ClientName(DataService service) => $"{service.ToString().ToLowerInvariant()}-status";

    /// <summary>The service's status, at most 5 seconds old; null when it can't be had or is stale.</summary>
    public async Task<DataStatus?> GetAsync(DataService service, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(service, out var cached) && now - cached.At < CacheFor)
        {
            return cached.Status;
        }

        var status = await new DataStatusClient(clients.CreateClient(ClientName(service))).GetAsync(cancellationToken);
        if (status is not null && !status.IsFresh(now))
        {
            status = null;
        }

        _cache[service] = (now, status);
        return status;
    }

    /// <summary>The service's status when it says it isn't serving (being built, loading, or waiting to try again).</summary>
    public async Task<DataStatus?> NotServingAsync(DataService service, CancellationToken cancellationToken) =>
        await GetAsync(service, cancellationToken) is { Serving: false } status ? status : null;

    /// <summary>For the UI: whether trips and the address search are ready, or how far their data is.</summary>
    public static ServiceReadiness Readiness(DataStatus? status) =>
        status is null
            ? new ServiceReadiness("unknown", true, null, null, null)
            : new ServiceReadiness(status.State, status.Serving, status.Stage, status.Percent, status.DataDate);
}

public static class DataServiceRegistration
{
    /// <summary>Clients for the two status servers: an answer within a second, or none.</summary>
    public static IServiceCollection AddDataServiceMonitor(this IServiceCollection services, Uri routingStatusUrl, Uri geocodingStatusUrl)
    {
        foreach (var (service, url) in new[] { (DataService.Routing, routingStatusUrl), (DataService.Geocoding, geocodingStatusUrl) })
        {
            if (!url.IsAbsoluteUri || !url.AbsolutePath.EndsWith('/'))
            {
                throw new InvalidOperationException($"{service}:StatusUrl must be an absolute URL ending with '/'.");
            }

            services.AddHttpClient(DataServiceMonitor.ClientName(service), client =>
            {
                client.BaseAddress = url;
                client.Timeout = TimeSpan.FromSeconds(1);
            });
        }

        services.AddSingleton<DataServiceMonitor>();
        return services;
    }

    /// <summary>
    /// 503 for a service that's down or whose data is being prepared, with its <c>service</c>, <c>state</c>,
    /// <c>stage</c> and <c>percent</c> when its status says them.
    /// </summary>
    public static ProblemHttpResult Unavailable(DataService service, DataStatus? status, string title, string detail)
    {
        var extensions = new Dictionary<string, object?> { ["service"] = service.ToString().ToLowerInvariant() };
        if (status is not null)
        {
            extensions["state"] = status.State;
            extensions["stage"] = status.Stage;
            extensions["percent"] = status.Percent;
            detail = status.Serving
                ? detail
                : $"Its data is being prepared ({Describe(status)}); it's ready in a few minutes.";
        }

        return TypedResults.Problem(detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: title, extensions: extensions);
    }

    /// <summary>"building: downloading, 45 %", for logs, problems and the health check.</summary>
    public static string Describe(DataStatus status) =>
        status.Stage is { } stage
            ? $"{status.State}: {stage}{(status.Percent is { } percent ? $", {percent} %" : "")}"
            : status.State;

    /// <summary>The status's fields for a health check's data.</summary>
    public static IReadOnlyDictionary<string, object> HealthData(DataStatus status)
    {
        var data = new Dictionary<string, object> { ["state"] = status.State };
        if (status.Stage is { } stage)
        {
            data["stage"] = stage;
        }

        if (status.Percent is { } percent)
        {
            data["percent"] = percent;
        }

        if (status.DataDate is { } date)
        {
            data["dataDate"] = date;
        }

        if (status.LastError is { } error)
        {
            data["lastError"] = error;
        }

        return data;
    }

    public static IEndpointRouteBuilder MapDataServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/services", Services)
            .WithTags("Services")
            .WithSummary("Whether trips and the address search are ready")
            .WithDescription(
                "For each of routing (road distances and trips) and geocoding (the address search): its state (`serving`, "
                + "`building`, `refreshing`, `starting`, `waiting`, or `unknown` when it doesn't say), whether it's ready, and while "
                + "its data is built, the stage and how far it is in percent, as the routing and geocoding containers report it.")
            .RequireRateLimiting(RateLimiting.PolicyName);
        return app;
    }

    private static async Task<Ok<DataServicesStatus>> Services(
        DataServiceMonitor monitor, HttpContext http, CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var routing = await monitor.GetAsync(DataService.Routing, cancellationToken);
        var geocoding = await monitor.GetAsync(DataService.Geocoding, cancellationToken);
        return TypedResults.Ok(new DataServicesStatus(DataServiceMonitor.Readiness(routing), DataServiceMonitor.Readiness(geocoding)));
    }
}
