using Essence.Contracts;
using Essence.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api.Endpoints;

public static class HoursEndpoints
{
    public static IEndpointRouteBuilder MapHoursEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/hours").WithTags("Hours").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/sources", Sources)
            .WithSummary("Where stations' opening hours come from, and how each source's runs are going")
            .WithDescription(
                "Each source that has run, by name: when it last succeeded (its hours fetched and written, changed or not) and "
                + "was last tried, what came of that (`succeeded`, `unchanged`, `rejected` when the answer looked wrong and the "
                + "last good hours kept serving, or `failed`) and why, its failures in a row, how many stations it has hours "
                + "for, and the version of the download they come from. Sources are fetched once a week, at night.");
        return app;
    }

    private static async Task<Ok<IReadOnlyList<HoursSourceStatus>>> Sources(EssenceDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await HoursSources.LoadAsync(db, cancellationToken));
}
