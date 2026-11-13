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

/// <summary>
/// The <c>ignoreHours</c> parameter of <c>/stations/best</c> and <c>/trips/best</c>: the stations whose hours the device
/// corrected, which the server's hours never leave out.
/// </summary>
public static class IgnoredHours
{
    /// <summary>At most this many ids.</summary>
    public const int MaxIds = 50;

    public const string Error = "Station ids (whole numbers above 0) separated by commas, at most 50.";

    /// <summary>
    /// The ids in <paramref name="text"/>, separated by commas (spaces around them allowed); none when it's missing or blank.
    /// Null when it isn't that: an id that isn't a whole number above 0, an empty one ("1,,2"), or more than
    /// <see cref="MaxIds"/>.
    /// </summary>
    public static HashSet<long>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > MaxIds)
        {
            return null;
        }

        var ids = new HashSet<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return null;
            }

            ids.Add(id);
        }

        return ids;
    }
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
