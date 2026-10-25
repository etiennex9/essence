using Essence.Ranking;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Essence.Api;

/// <summary>The <c>Geocoding</c> configuration section: where the Photon server (the geocoding container) answers.</summary>
public sealed class GeocodingOptions
{
    public const string SectionName = "Geocoding";

    public Uri BaseUrl { get; set; } = new("http://geocoding:2322/");
}

public static class PhotonGeocoding
{
    /// <summary>
    /// An answer within this, or none: no retry. Typing goes on meanwhile, and the next keystroke's search replaces this
    /// one. It covers the name's resolution too, which takes seconds for a stopped container on Docker Desktop.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The <see cref="PhotonClient"/> for the geocoding container, with a plain timeout and no resilience pipeline.</summary>
    public static IServiceCollection AddPhotonGeocoding(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(GeocodingOptions.SectionName).Get<GeocodingOptions>() ?? new GeocodingOptions();
        if (!options.BaseUrl.IsAbsoluteUri || !options.BaseUrl.AbsolutePath.EndsWith('/'))
        {
            throw new InvalidOperationException("Geocoding:BaseUrl must be an absolute URL ending with '/'.");
        }

        services.AddHttpClient<PhotonClient>(client =>
            {
                client.BaseAddress = options.BaseUrl;
                client.Timeout = Timeout;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        return services;
    }
}

/// <summary>
/// Whether the geocoding container answers. Registered as Degraded: the API answers without it (no suggestions, no
/// addresses for pins; trips still go to pins and saved places), so <c>/health</c> stays 200 and says so.
/// </summary>
public sealed class GeocodingHealthCheck(PhotonClient photon) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await photon.StatusAsync(cancellationToken);
            return HealthCheckResult.Healthy("The address search answers.");
        }
        catch (PhotonException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, $"The address search doesn't answer: {ex.Message}");
        }
    }
}

internal static class GeocodingLog
{
    /// <summary>The logger category for address search failures.</summary>
    public const string Category = "Essence.Api.Geocoding";

    /// <summary>
    /// A failed call to the geocoding server: a bug here as an error, the server being down as a warning. The message holds
    /// what went wrong, never the request, whose query says what users typed and where they are.
    /// </summary>
    public static void LogPhotonFailure(this ILogger logger, PhotonException ex)
    {
        if (ex.Failure == PhotonFailure.Bug)
        {
            logger.LogError("The address search refused a request: {Message}", ex.Message);
        }
        else
        {
            logger.LogWarning("The address search is unavailable: {Message}", ex.Message);
        }
    }
}
