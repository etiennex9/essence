namespace Essence.Ranking;

/// <summary>
/// One price of a station's fuel over [<see cref="From"/>, <see cref="To"/>), in UTC ticks: a <c>price_history</c> row,
/// kept small (24 bytes) since the trends hold about 500,000 of them.
/// </summary>
/// <param name="To"><see cref="Open"/> while it's still the price.</param>
/// <param name="Tenths">The price in tenths of a cent; <see cref="Unavailable"/> when the fuel was unavailable.</param>
public readonly record struct PriceSpan(long From, long To, int Tenths)
{
    public const long Open = long.MaxValue;

    public const int Unavailable = -1;

    public bool IsAvailable => Tenths >= 0;

    /// <summary>
    /// Whether it counts as of <paramref name="asOf"/>: it was still the price then, or it lasted at least
    /// <see cref="TrendRules.Settled"/>. A price corrected within minutes (a flip-flop) doesn't.
    /// </summary>
    public bool IsSettled(long asOf) => To > asOf || To - From >= TrendRules.Settled.Ticks;

    public static int ToTenths(decimal? priceCents, bool isAvailable) =>
        isAvailable && priceCents is { } cents ? (int)Math.Round(cents * 10, MidpointRounding.AwayFromZero) : Unavailable;

    public static decimal ToCents(int tenths) => tenths / 10m;
}

/// <summary>The prices of one station's fuel, oldest first, as the trends read them.</summary>
public sealed class StationPrices(PriceSpan[] spans)
{
    public IReadOnlyList<PriceSpan> Spans => spans;

    internal PriceSpan this[int index] => spans[index];

    internal int Count => spans.Length;

    /// <summary>The index of the last span starting at or before <paramref name="ticks"/>; -1 when none does.</summary>
    internal int LastStartingAtOrBefore(long ticks)
    {
        int low = 0, high = spans.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (spans[middle].From <= ticks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - 1;
    }

    /// <summary>
    /// The settled span (<see cref="PriceSpan.IsSettled"/> as of <paramref name="asOf"/>) that gave the price at
    /// <paramref name="ticks"/>: the last one starting at or before it. -1 when there's none, or when the station had no
    /// price row at all then (the fuel wasn't sold).
    /// </summary>
    internal int SettledAt(long ticks, long asOf)
    {
        var last = LastStartingAtOrBefore(ticks);
        if (last < 0 || spans[last].To <= ticks)
        {
            return -1;
        }

        for (var i = last; i >= 0; i--)
        {
            if (spans[i].IsSettled(asOf))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The price at <paramref name="ticks"/> in tenths of a cent, as of <paramref name="asOf"/>; null when it had none.</summary>
    public int? TenthsAt(long ticks, long asOf) =>
        SettledAt(ticks, asOf) is var index and >= 0 && spans[index].IsAvailable ? spans[index].Tenths : null;
}

/// <summary>A station of the area, with its prices for the fuel.</summary>
public sealed record AreaStation(long Id, StationPrices Prices);
