using System.Text;
using Essence.Data;

namespace Essence.Ingest.Hours;

/// <summary>
/// The brand a station belongs to, for matching sources' records to stations: the feed's brand, or a source's, folded
/// (lowercase, accents gone, other characters a dash: "Pétro-Canada" and "Petro Canada" are <c>petro-canada</c>), with
/// the names one network goes by made one. A brand this doesn't know is its folded name: two sources that write it the
/// same way still match.
/// </summary>
public static class BrandFamily
{
    public const string Costco = "costco";
    public const string CoucheTard = "couche-tard";
    public const string Esso = "esso";
    public const string PetroCanada = "petro-canada";
    public const string Shell = "shell";
    public const string Ultramar = "ultramar";

    /// <summary>The other names of the networks, folded, as the feed, the brands' locators and OpenStreetMap write them.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Imperial Oil's stations: the locators and OpenStreetMap use either.
        ["mobil"] = Esso,
        ["esso-express"] = Esso,
        ["imperial-oil"] = Esso,
        // Couche-Tard's stations in Québec are Circle K's elsewhere, and its locator is Circle K's.
        ["circle-k"] = CoucheTard,
        ["circlek"] = CoucheTard,
        ["couche-tard-inc"] = CoucheTard,
        ["alimentation-couche-tard"] = CoucheTard,
        ["costco-wholesale"] = Costco,
        ["costco-gasoline"] = Costco,
        ["costco-gas"] = Costco,
        ["essence-costco"] = Costco,
        ["petrocanada"] = PetroCanada,
        ["shell-express"] = Shell,
        ["ultramar-express"] = Ultramar,
        ["irving-oil"] = "irving",
        ["harnois-energies"] = "harnois",
        ["canadiantire"] = "canadian-tire",
        ["canadian-tire-gas"] = "canadian-tire",
        ["essence-de-canadian-tire"] = "canadian-tire",
    };

    /// <summary>
    /// "Pétro-T" is <c>petro-t</c>, "Mobil" is <c>esso</c>, "Circle K" is <c>couche-tard</c>; null for no brand (the feed's
    /// "Aucun" included).
    /// </summary>
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
