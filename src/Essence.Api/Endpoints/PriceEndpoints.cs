using System.ComponentModel;
using Essence.Contracts;
using Essence.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api.Endpoints;

public static class PriceEndpoints
{
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapPriceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/prices").WithTags("Prices").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/current", CurrentPrices)
            .WithSummary("Current prices at active stations, cheapest first")
            .WithDescription("Unavailable fuels (no price) are left out unless `includeUnavailable=true`.");
        return app;
    }

    private static async Task<Results<Ok<List<PriceListing>>, ValidationProblem>> CurrentPrices(
        EssenceDbContext db,
        CancellationToken cancellationToken,
        [Description("Régulier, Super or Diesel (accents optional).")] string? fuel = null,
        [Description("Exact region name, case-insensitive.")] string? region = null,
        [Description("Exact brand, case-insensitive.")] string? brand = null,
        [Description("`price` (cheapest first) or `-price` (most expensive first).")] string sort = "price",
        bool includeUnavailable = false,
        int limit = 50)
    {
        var errors = new QueryErrors()
            .Require(sort is "price" or "-price", nameof(sort), "Must be `price` or `-price`.")
            .Require(limit is >= 1 and <= MaxLimit, nameof(limit), $"Must be between 1 and {MaxLimit}.");
        var fuelType = await StationEndpoints.ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var query = db.CurrentPrices.Where(p => p.Station.IsActive);
        if (fuelType is not null)
        {
            var fuelId = fuelType.Id;
            query = query.Where(p => p.FuelTypeId == fuelId);
        }

        if (!string.IsNullOrWhiteSpace(region))
        {
            var lowered = region.Trim().ToLowerInvariant();
            query = query.Where(p => p.Station.Region.ToLower() == lowered);
        }

        if (!string.IsNullOrWhiteSpace(brand))
        {
            var lowered = brand.Trim().ToLowerInvariant();
            query = query.Where(p => p.Station.Brand!.ToLower() == lowered);
        }

        if (!includeUnavailable)
        {
            query = query.Where(p => p.IsAvailable && p.PriceCents != null);
        }

        query = sort == "-price"
            ? query.OrderByDescending(p => p.PriceCents).ThenBy(p => p.StationId)
            : query.OrderBy(p => p.PriceCents).ThenBy(p => p.StationId);

        var rows = await query
            .Take(limit)
            .Select(p => new PriceListing(
                p.StationId, p.Station.Name, p.Station.Brand, p.Station.Address, p.Station.Region,
                p.Station.Latitude, p.Station.Longitude, p.FuelType.Code, p.PriceCents, p.IsAvailable, p.Since))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(rows);
    }
}
