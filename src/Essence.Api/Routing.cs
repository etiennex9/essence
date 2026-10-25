using Essence.Ranking;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Essence.Api;

/// <summary>The <c>Routing</c> configuration section: where the OSRM server (the routing container) answers.</summary>
public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    public Uri BaseUrl { get; set; } = new("http://routing:5000/");
}

public static class OsrmRouting
{
    /// <summary>An answer within this, or the attempt is given up; one retry. A trip takes three calls.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Connecting to the server, within an attempt.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The <see cref="OsrmClient"/> for the routing container, with a resilience pipeline: about 3 s per attempt and one
    /// retry, and a circuit breaker so that while the server is down, Best near you falls back to estimates at once. A
    /// stopped container's name can take seconds to fail to resolve (4 s on Docker Desktop), which no timeout here
    /// shortens: the breaker opens after the first few failures and tries again every 30 s.
    /// </summary>
    public static IServiceCollection AddOsrmRouting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RoutingOptions.SectionName).Get<RoutingOptions>() ?? new RoutingOptions();
        if (!options.BaseUrl.IsAbsoluteUri || !options.BaseUrl.AbsolutePath.EndsWith('/'))
        {
            throw new InvalidOperationException("Routing:BaseUrl must be an absolute URL ending with '/'.");
        }

        services.AddHttpClient<OsrmClient>(client =>
            {
                client.BaseAddress = options.BaseUrl;
                client.Timeout = Timeout.InfiniteTimeSpan; // The resilience pipeline owns timeouts.
                client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The server is a container away: one that doesn't answer a connection fails the attempt early.
                ConnectTimeout = ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = AttemptTimeout;
                o.Retry.MaxRetryAttempts = 1;
                o.Retry.Delay = TimeSpan.FromMilliseconds(200);
                o.TotalRequestTimeout.Timeout = AttemptTimeout * 2 + TimeSpan.FromSeconds(1);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                o.CircuitBreaker.MinimumThroughput = 4;
                o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
            });
        return services;
    }
}

/// <summary>
/// Whether the routing container answers. Registered as Degraded: the API answers without it (Best near you estimates
/// distances, trips answer 503), so <c>/health</c> stays 200 and says so.
/// </summary>
public sealed class RoutingHealthCheck(OsrmClient osrm) : IHealthCheck
{
    private static readonly LatLon Montreal = new(45.5017, -73.5673);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await osrm.NearestAsync(Montreal, cancellationToken);
            return HealthCheckResult.Healthy("The routing server answers.");
        }
        catch (OsrmException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, $"The routing server doesn't answer: {ex.Message}");
        }
    }
}

internal static class RoutingLog
{
    /// <summary>The logger category for routing failures.</summary>
    public const string Category = "Essence.Api.Routing";

    /// <summary>
    /// A failed call to the routing server: a bug here as an error, the server being down as a warning. The message holds
    /// OSRM's code and text, never the request, whose path says where users are.
    /// </summary>
    public static void LogOsrmFailure(this ILogger logger, OsrmException ex)
    {
        if (ex.Failure == OsrmFailure.Bug)
        {
            logger.LogError("The routing server refused a request: {Message}", ex.Message);
        }
        else if (ex.Failure == OsrmFailure.Unavailable)
        {
            logger.LogWarning("The routing server is unavailable: {Message}", ex.Message);
        }
    }
}
