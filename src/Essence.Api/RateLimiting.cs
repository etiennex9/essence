using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Essence.Api;

/// <summary>The <c>RateLimiting</c> configuration section: a fixed window per client IP.</summary>
public sealed class RateLimitingOptions
{
    public int PermitLimit { get; set; } = 120;
    public int WindowSeconds { get; set; } = 60;

    /// <summary>The address search's own window (<c>RateLimiting:Search</c>), so typing doesn't eat the shared one.</summary>
    public SearchRateLimitingOptions Search { get; set; } = new();
}

/// <summary>
/// <c>RateLimiting:Search</c>. A search goes out at most every 300 ms while typing, and fewer once earlier answers are
/// cached: 60 a minute covers someone typing a few addresses in a row, and a pin's address with each press and hold.
/// </summary>
public sealed class SearchRateLimitingOptions
{
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimiting
{
    public const string PolicyName = "per-ip";

    /// <summary>The address search and reverse lookups (<c>/places/</c>), counted apart from the rest.</summary>
    public const string SearchPolicyName = "search-per-ip";

    /// <summary>
    /// <c>/services</c>, counted apart too, so that a page asking it can't use up the rest: the map asks every 10 seconds
    /// while trips or the search are being prepared, and after a 503. 60 a minute.
    /// </summary>
    public const string ServicesPolicyName = "services-per-ip";

    public static IServiceCollection AddPerIpRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            // Behind Essence.Web's nginx, RemoteIpAddress is the client's: Program.cs applies the forwarded headers.
            limiter.AddPolicy(PolicyName, http => PerIp(http, options.PermitLimit, options.WindowSeconds));
            limiter.AddPolicy(SearchPolicyName, http => PerIp(http, options.Search.PermitLimit, options.Search.WindowSeconds));
            limiter.AddPolicy(ServicesPolicyName, http => PerIp(http, 60, 60));
        });
    }

    private static RateLimitPartition<string> PerIp(HttpContext http, int permitLimit, int windowSeconds) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
            });
}
