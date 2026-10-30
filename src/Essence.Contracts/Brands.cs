using System.Globalization;
using System.Text;

namespace Essence.Contracts;

/// <summary>Brands as the feed spells them, compared by their slug. Shared by the API and the UI.</summary>
public static class Brands
{
    /// <summary>Costco's stations sell to members only.</summary>
    public const string CostcoSlug = "costco";

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
}
