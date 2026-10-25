using Essence.Contracts;

namespace Essence.UI.Services;

/// <param name="Name">The brand as the feed spells it; null for stations without one.</param>
/// <param name="Monogram">One or two letters shown when there's no logo; empty for stations without a brand.</param>
/// <param name="LogoUrl">Relative URL of the logo file, or null to show the monogram.</param>
public sealed record BrandStyle(string? Name, string Slug, string Monogram, string Background, string Foreground, string? LogoUrl);

/// <summary>
/// How each brand looks on the map. Hand-picked colors for the biggest brands (about 90% of stations), a stable color for the
/// others, by their <see cref="Brands.Slugify"/> slug. Logos are optional drop-in files: add <c>wwwroot/brands/{slug}.svg</c>
/// and its slug to <see cref="LogoSlugs"/>.
/// </summary>
public static class BrandCatalog
{
    public const string LogoBasePath = "_content/Essence.UI/brands/";

    /// <summary>Slugs that have a file in <c>wwwroot/brands/</c>. Listed so missing logos are never requested.</summary>
    private static readonly HashSet<string> LogoSlugs = [];

    private static readonly Dictionary<string, (string Background, string Foreground)> KnownColors = new()
    {
        ["esso"] = ("#D7262E", "#FFFFFF"),
        ["petro-canada"] = ("#8E1B2C", "#FFFFFF"),
        ["shell"] = ("#FFD500", "#C8102E"),
        ["ultramar"] = ("#1E4FA0", "#FFFFFF"),
        ["harnois"] = ("#1F6F43", "#FFFFFF"),
        ["irving"] = ("#00539B", "#FFFFFF"),
        ["sonic"] = ("#5B2C83", "#FFFFFF"),
        ["couche-tard"] = ("#EE7A1E", "#FFFFFF"),
        ["crevier"] = ("#0D7C86", "#FFFFFF"),
        ["canadian-tire"] = ("#B0151B", "#FFFFFF"),
        ["costco"] = ("#005DAA", "#FFFFFF"),
    };

    /// <summary>Colors for brands without hand-picked ones, chosen by a stable hash of the slug.</summary>
    private static readonly (string Background, string Foreground)[] FallbackColors =
    [
        ("#3C3489", "#FFFFFF"),
        ("#085041", "#FFFFFF"),
        ("#993C1D", "#FFFFFF"),
        ("#72243E", "#FFFFFF"),
        ("#0C447C", "#FFFFFF"),
        ("#3B6D11", "#FFFFFF"),
        ("#854F0B", "#FFFFFF"),
        ("#444441", "#FFFFFF"),
    ];

    private static readonly BrandStyle NoBrand = new(null, "", "", "#888780", "#FFFFFF", null);

    public static BrandStyle For(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand))
        {
            return NoBrand;
        }

        var slug = Brands.Slugify(brand);
        var (background, foreground) = KnownColors.TryGetValue(slug, out var known)
            ? known
            : FallbackColors[StableHash(slug) % (uint)FallbackColors.Length];
        var logoUrl = LogoSlugs.Contains(slug) ? $"{LogoBasePath}{slug}.svg" : null;
        return new BrandStyle(brand.Trim(), slug, Monogram(brand), background, foreground, logoUrl);
    }

    /// <summary>The first letter of the first two words: "Petro-Canada" → "PC", "R.L." → "RL", "Esso" → "E".</summary>
    public static string Monogram(string brand)
    {
        var words = brand.Split([' ', '-', '.', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>FNV-1a: unlike <see cref="string.GetHashCode()"/>, the same on every run.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
