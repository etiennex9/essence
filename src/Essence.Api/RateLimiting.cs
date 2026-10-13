using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Essence.Api;

/// <summary>The <c>RateLimiting</c> configuration section: a fixed window per client IP.</summary>
public sealed class RateLimitingOptions
{
    public int PermitLimit { get; set; } = 120;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimiting
{
    public const string PolicyName = "per-ip";

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
            // Behind a reverse proxy, configure forwarded headers so RemoteIpAddress is the client's.
            limiter.AddPolicy(PolicyName, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                }));
        });
    }
}
