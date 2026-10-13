using System.Globalization;
using System.Text;

namespace Essence.Data;

/// <summary>
/// Builds a station's natural key from its address and postal code, the only stable identity the feed offers.
/// Address: trimmed, whitespace collapsed, lowercase, accents removed. Postal code: uppercase, no whitespace.
/// </summary>
public static class StationKey
{
    public static string Create(string? address, string? postalCode) =>
        $"{NormalizeAddress(address)}|{NormalizePostalCode(postalCode)}";

    public static string NormalizeAddress(string? address) => Fold(CollapseWhitespace(address));

    public static string NormalizePostalCode(string? postalCode)
    {
        if (string.IsNullOrEmpty(postalCode))
        {
            return "";
        }

        var builder = new StringBuilder(postalCode.Length);
        foreach (var c in postalCode)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Lowercases and strips diacritics ("Régulier" → "regulier") for accent-insensitive comparisons.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Trims and replaces every run of whitespace (including non-breaking spaces) with a single space.</summary>
    public static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
