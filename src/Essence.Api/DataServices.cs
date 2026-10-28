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
/// What the routing and geocoding containers say they're doing (their supervisors' GET :9090/status), asked every 5
/// seconds in the background, so that requests only read it. A service that says it isn't serving (its data is being
/// built, or loaded) isn't called: Best near you estimates, trips and the address search answer 503 with its progress,
/// and the routing client's circuit breaker isn't tripped by a build. A status that can't be had (the container is down,
/// or an older image) or is stale leaves things as before: the service is called, and its failure counts.
/// </summary>
public sealed class DataServiceMonitor(IHttpClientFactory clients, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    /// <summary>Older than this, the status is asked on the request: before the first poll, or when the polls stalled.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<DataService, (DateTimeOffset At, DataStatus? Status)> _latest = new();
    private readonly Dictionary<DataService, Task<DataStatus?>> _asking = [];
    private readonly Lock _lock = new();

    public static string ClientName(DataService service) => $"{service.ToString().ToLowerInvariant()}-status";

    /// <summary>The service's status, as last asked; null when it can't be had or is stale.</summary>
    public async Task<DataStatus?> GetAsync(DataService service, CancellationToken cancellationToken) =>
        _latest.TryGetValue(service, out var latest) && time.GetUtcNow() - latest.At < MaxAge
            ? Fresh(latest.Status)
            : await RefreshAsync(service).WaitAsync(cancellationToken);

    /// <summary>
    /// Asks the service now, as after a call it refused: it may have just stopped serving (a swap of its data). One
    /// request at a time per service, which whoever asks meanwhile shares.
    /// </summary>
    public Task<DataStatus?> RefreshAsync(DataService service)
    {
        lock (_lock)
        {
            if (!_asking.TryGetValue(service, out var asking) || asking.IsCompleted)
            {
                _asking[service] = asking = AskAsync(service);
            }

            return asking;
        }
    }

    /// <summary>
    /// A call the server refused: it may have just stopped for new data, so its status is asked again in the background,
    /// for the requests that follow; this one gets the status last known, without waiting.
    /// </summary>
    public Task<DataStatus?> RefusedAsync(DataService service)
    {
        _ = RefreshAsync(service);
        return GetAsync(service, CancellationToken.None);
    }

    /// <summary>The service's status when it says it isn't serving (being built, loading, or waiting to try again).</summary>
    public async Task<DataStatus?> NotServingAsync(DataService service, CancellationToken cancellationToken) =>
        await GetAsync(service, cancellationToken) is { Serving: false } status ? status : null;

    /// <summary>For the UI: whether trips and the address search are ready, or how far their data is.</summary>
    public static ServiceReadiness Readiness(DataStatus? status) =>
        status is null
            ? new ServiceReadiness("unknown", true, null, null, null, null, null)
            : new ServiceReadiness(
                status.State, status.Serving, status.Stage, status.Percent, status.DataDate, status.LastError, status.NextAttemptAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.WhenAll(RefreshAsync(DataService.Routing), RefreshAsync(DataService.Geocoding));
                await Task.Delay(PollEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<DataStatus?> AskAsync(DataService service)
    {
        var status = await new DataStatusClient(clients.CreateClient(ClientName(service))).GetAsync(CancellationToken.None);
        _latest[service] = (time.GetUtcNow(), status);
        return Fresh(status);
    }

    private DataStatus? Fresh(DataStatus? status) => status is not null && status.IsFresh(time.GetUtcNow()) ? status : null;
}

public static class DataServiceRegistration
{
    /// <summary>Clients for the two status servers (an answer within a second, or none), and the monitor that asks them.</summary>
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
        services.AddHostedService(provider => provider.GetRequiredService<DataServiceMonitor>());
        return services;
    }

    /// <summary>
    /// 503 for a service that's down or whose data is being prepared, with its <c>service</c>, <c>state</c>,
    /// <c>stage</c>, <c>percent</c>, <c>lastError</c> and <c>nextAttemptAt</c> when its status says them.
    /// </summary>
    public static ProblemHttpResult Unavailable(DataService service, DataStatus? status, string title, string detail)
    {
        var extensions = new Dictionary<string, object?> { ["service"] = service.ToString().ToLowerInvariant() };
        if (status is not null)
        {
            extensions["state"] = status.State;
            extensions["stage"] = status.Stage;
            extensions["percent"] = status.Percent;
            extensions["lastError"] = status.LastError;
            extensions["nextAttemptAt"] = status.NextAttemptAt;
            detail = status.Serving ? detail : NotServing(status);
        }

        return TypedResults.Problem(detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: title, extensions: extensions);
    }

    /// <summary>Why a service that isn't serving isn't: its data is being prepared, or that failed and is tried again later.</summary>
    public static string NotServing(DataStatus status) =>
        status is { State: "waiting", NextAttemptAt: { } next }
            ? $"{(status.LastError is { } error ? $"It failed ({error}); it" : "It")} tries again at {next.UtcDateTime:yyyy-MM-dd HH:mm} UTC."
            : $"Its data is being prepared ({Describe(status)}); it's ready in a few minutes.";

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

        if (status.NextAttemptAt is { } next)
        {
            data["nextAttemptAt"] = next;
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
                + "`building`, `refreshing`, `starting`, `waiting`, or `unknown` when it doesn't say), whether it's ready, while "
                + "its data is built the stage and how far it is in percent, and while it waits to try again what failed and when.")
            .RequireRateLimiting(RateLimiting.ServicesPolicyName);
        return app;
    }

    private static async Task<Ok<DataServicesStatus>> Services(
        DataServiceMonitor monitor, HttpContext http, CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var routing = monitor.GetAsync(DataService.Routing, cancellationToken);
        var geocoding = monitor.GetAsync(DataService.Geocoding, cancellationToken);
        return TypedResults.Ok(new DataServicesStatus(DataServiceMonitor.Readiness(await routing), DataServiceMonitor.Readiness(await geocoding)));
    }
}
