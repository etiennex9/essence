using Essence.Data;
using Essence.Data.Entities;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Essence.Api;

/// <summary>Healthy when the database answers and the last successful import is recent enough.</summary>
public sealed class IngestHealthCheck(EssenceDbContext db, TimeProvider timeProvider, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var maxAge = TimeSpan.FromMinutes(configuration.GetValue("Health:MaxImportAgeMinutes", 15));

        DateTimeOffset? lastImportAt;
        try
        {
            lastImportAt = await db.Imports
                .Where(i => i.Status == ImportStatus.Succeeded)
                .MaxAsync(i => (DateTimeOffset?)i.FetchedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database is unreachable.", ex);
        }

        if (lastImportAt is not { } last)
        {
            return HealthCheckResult.Unhealthy("No successful import yet.");
        }

        var age = timeProvider.GetUtcNow() - last;
        var data = new Dictionary<string, object>
        {
            ["lastImportAt"] = last,
            ["ageMinutes"] = Math.Round(age.TotalMinutes, 1),
            ["maxAgeMinutes"] = maxAge.TotalMinutes,
        };
        return age <= maxAge
            ? HealthCheckResult.Healthy("The last import is recent.", data)
            : HealthCheckResult.Unhealthy("The last successful import is too old.", data: data);
    }

    /// <summary>JSON body for <c>/health</c>. Exceptions are logged, not returned.</summary>
    public static Task WriteResponseAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data,
            }),
        });

    public static HealthCheckOptions EndpointOptions { get; } = new() { ResponseWriter = WriteResponseAsync };
}
