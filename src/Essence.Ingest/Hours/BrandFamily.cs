using System.Text;
using Essence.Data;

namespace Essence.Ingest.Hours;

/// <summary>
/// The brand a station belongs to, for matching sources' records to stations: the feed's brand folded (lowercase, accents
/// gone, other characters a dash), with the brands that are one network under two names made one.
/// </summary>
public static class BrandFamily
{
    public const string Costco = "costco";

    /// <summary>Brands sold under another name by the same network, by their folded name.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Imperial Oil's stations: the locators and OpenStreetMap use either.
        ["mobil"] = "esso",
        // Couche-Tard's stations in Québec are Circle K's elsewhere, and its locator is Circle K's.
        ["circle-k"] = "couche-tard",
        ["costco-wholesale"] = Costco,
        ["petrocanada"] = "petro-canada",
    };

    /// <summary>"Pétro-T" is <c>petro-t</c>, "Mobil" is <c>esso</c>; null for no brand (the feed's "Aucun" included).</summary>
    public static string? Of(string? brand)
    {
        var folded = Fold(brand);
        if (folded.Length == 0 || folded == "aucun")
        {
            return null;
        }

        return Aliases.TryGetValue(folded, out var family) ? family : folded;
    }

    private static string Fold(string? brand)
    {
        var text = StationKey.Fold(brand);
        var builder = new StringBuilder(text.Length);
        var pendingDash = false;
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(c);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }
}
