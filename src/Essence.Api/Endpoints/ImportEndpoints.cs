using Essence.Contracts;
using Essence.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api.Endpoints;

public static class ImportEndpoints
{
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports").WithTags("Imports").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/", ListImports).WithSummary("Recent imports of the feed, newest first, with their statistics");
        return app;
    }

    private static async Task<Results<Ok<List<ImportDto>>, ValidationProblem>> ListImports(
        EssenceDbContext db,
        CancellationToken cancellationToken,
        int limit = 20)
    {
        var errors = new QueryErrors().Require(limit is >= 1 and <= MaxLimit, nameof(limit), $"Must be between 1 and {MaxLimit}.");
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var imports = await db.Imports.OrderByDescending(i => i.Id).Take(limit).ToListAsync(cancellationToken);
        return TypedResults.Ok(imports
            .Select(i => new ImportDto(
                i.Id, i.Status.ToString().ToLowerInvariant(), i.Source.ToString().ToLowerInvariant(), i.GeneratedAt, i.FetchedAt, i.DelaySeconds,
                i.StationsTotal, i.StationsAdded, i.StationsUpdated, i.StationsDeactivated, i.PricesChanged, i.Warnings,
                i.Sha256, FirstLine(i.Error)))
            .ToList());
    }

    /// <summary>The stored error includes a stack trace; the public API only shows the exception type and message.</summary>
    private static string? FirstLine(string? error) => error?.Split('\n', 2)[0].TrimEnd('\r');
}
