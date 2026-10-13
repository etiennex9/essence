using System.Globalization;

namespace Essence.Ingest.Feed;

public static class PriceParser
{
    /// <summary>Largest value a <c>numeric(5,1)</c> column holds.</summary>
    private const decimal MaxCents = 9999.9m;

    /// <summary>
    /// Parses a feed price such as "199.4¢" into cents per litre. Null or blank means "no price" and succeeds with null.
    /// Values are rounded to one decimal, like PostgreSQL does on insert, so they compare equal to what's stored.
    /// </summary>
    public static bool TryParse(string? text, out decimal? cents)
    {
        cents = null;
        if (text is null)
        {
            return true;
        }

        var span = text.AsSpan().Trim();
        if (span.EndsWith("¢"))
        {
            span = span[..^1].TrimEnd();
        }

        if (span.IsEmpty)
        {
            return true;
        }

        if (!decimal.TryParse(span, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        value = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (value > MaxCents)
        {
            return false;
        }

        cents = value;
        return true;
    }
}
