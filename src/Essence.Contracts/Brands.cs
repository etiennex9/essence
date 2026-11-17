using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Essence.Contracts;

/// <summary>Brands as the feed spells them, compared by their slug. Shared by the API and the UI.</summary>
public static class Brands
{
    /// <summary>Costco's stations sell to members only.</summary>
    public const string CostcoSlug = "costco";

    /// <summary>
    /// The words the feed writes for a station without a brand, by slug: "Aucun" (which the ingest makes no brand) and
    /// "Indépendant" (which it keeps, three stations' in November 2026), in either language.
    /// </summary>
    private static readonly HashSet<string> NoBrandSlugs = new(StringComparer.Ordinal) { "aucun", "independant", "independante", "independent" };

    /// <summary>Lowercase, accents removed, every run of other characters turned into one dash: "Pétro-T" → "petro-t".</summary>
    public static string Slugify(string brand)
    {
        var builder = new StringBuilder(brand.Length);
        var pendingDash = false;
        foreach (var c in brand.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether a station of this brand sells to members only (Costco).</summary>
    public static bool IsMembersOnly(string? brand) => !string.IsNullOrWhiteSpace(brand) && Slugify(brand) == CostcoSlug;

    /// <summary>
    /// It names no brand: missing, blank, or the feed's word for an independent station ("Indépendant", "Aucun", accents
    /// and case aside). Such a station has no brand to give OpenStreetMap, whose <c>brand</c> is a chain's.
    /// </summary>
    public static bool IsNone([NotNullWhen(false)] string? brand) => string.IsNullOrWhiteSpace(brand) || NoBrandSlugs.Contains(Slugify(brand));
}
