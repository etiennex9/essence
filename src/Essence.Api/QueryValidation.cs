using Essence.Data;
using Essence.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>Collects query-string errors into a ValidationProblem (400) response.</summary>
internal sealed class QueryErrors
{
    private readonly Dictionary<string, string[]> _errors = [];

    public bool Any => _errors.Count > 0;

    public QueryErrors Require(bool condition, string parameter, string message)
    {
        if (!condition)
        {
            _errors[parameter] = [message];
        }

        return this;
    }

    public ValidationProblem ToProblem() => TypedResults.ValidationProblem(_errors);
}

internal static class FuelLookup
{
    /// <summary>Finds a fuel type by code, ignoring case and accents ("regulier" matches "Régulier").</summary>
    public static async Task<FuelType?> FindAsync(EssenceDbContext db, string code, CancellationToken cancellationToken)
    {
        var folded = StationKey.Fold(code.Trim());
        var fuelTypes = await db.FuelTypes.ToListAsync(cancellationToken);
        return fuelTypes.FirstOrDefault(f => StationKey.Fold(f.Code) == folded);
    }

    public static async Task<string> DescribeKnownAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        var codes = await db.FuelTypes.OrderBy(f => f.Id).Select(f => f.Code).ToListAsync(cancellationToken);
        return codes.Count == 0 ? "No fuel types are known yet." : "Known fuel types: " + string.Join(", ", codes) + ".";
    }
}
