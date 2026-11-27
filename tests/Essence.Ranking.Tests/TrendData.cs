using Essence.Hours;

namespace Essence.Ranking.Tests;

/// <summary>Price histories and imports to test the trends on, in Montréal's time zone.</summary>
internal static class TrendData
{
    public static TimeZoneInfo Zone => QuebecTime.Eastern;

    /// <summary>A moment on Montréal's clock (2026-11-02 and after: standard time, UTC−5).</summary>
    public static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        At(new DateOnly(2026, month, day), hour, minute, Zone);

    /// <summary>A moment on <paramref name="zone"/>'s clock.</summary>
    public static DateTimeOffset At(DateOnly day, int hour, int minute, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Imports every 5 minutes from <paramref name="from"/> to <paramref name="to"/>, none during the gaps.</summary>
    public static Coverage Imports(DateTimeOffset from, DateTimeOffset to, params (DateTimeOffset From, DateTimeOffset To)[] gaps) =>
        Coverage.Of(Slots(from, to).Where(at => !gaps.Any(g => at > g.From && at < g.To)));

    public static IEnumerable<DateTimeOffset> Slots(DateTimeOffset from, DateTimeOffset to)
    {
        for (var at = from; at <= to; at = at.AddMinutes(5))
        {
            yield return at;
        }
    }

    /// <summary>A station's prices: each change starts a price that holds until the next; the last is still the price. Null is unavailable.</summary>
    public static StationPrices Prices(params (DateTimeOffset At, decimal? Cents)[] changes)
    {
        var spans = new PriceSpan[changes.Length];
        for (var i = 0; i < changes.Length; i++)
        {
            spans[i] = new PriceSpan(
                changes[i].At.UtcTicks,
                i + 1 < changes.Length ? changes[i + 1].At.UtcTicks : PriceSpan.Open,
                PriceSpan.ToTenths(changes[i].Cents, changes[i].Cents is not null));
        }

        return new StationPrices(spans);
    }

    public static AreaStation Station(long id, params (DateTimeOffset At, decimal? Cents)[] changes) => new(id, Prices(changes));
}
