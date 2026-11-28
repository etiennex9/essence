namespace Essence.Ranking;

/// <summary>The area's prices at a moment: one per station that had a price then.</summary>
/// <param name="MedianCents">The lower of the two middle prices with an even count: always a price a station posted.</param>
public readonly record struct AreaSample(decimal LowestCents, decimal MedianCents, int Stations);

/// <summary>The area's median earlier and now, among the stations that had a price both times.</summary>
public sealed record AreaComparison(DateTimeOffset At, decimal MedianCents, decimal NowMedianCents, int Stations);

/// <summary>The lowest price of the last 24 hours.</summary>
/// <param name="From">When it started, or 24 hours ago if earlier.</param>
/// <param name="To">When it ended; null while it's still the price, or when it ended during a gap in the data.</param>
public sealed record AreaLowest(long StationId, decimal PriceCents, DateTimeOffset From, DateTimeOffset? To, bool StillNow);

/// <param name="AsOf">"Now": the latest import at or before the clock.</param>
/// <param name="SeriesFrom">The first sample of <paramref name="Series"/>, on a quarter-hour; the others follow every <see cref="TrendRules.Step"/>.</param>
/// <param name="Series">The last 24 hours, null where the data has a gap or no station had a price.</param>
public sealed record TrendFigures(
    DateTimeOffset AsOf,
    AreaSample? Now,
    AreaComparison? Yesterday,
    AreaComparison? LastWeek,
    AreaLowest? Low,
    DateTimeOffset SeriesFrom,
    IReadOnlyList<AreaSample?> Series);

/// <summary>How the area's prices moved: now, against yesterday and last week at the same time, and over the last 24 hours.</summary>
public static class Trends
{
    /// <summary>Comparisons need this many stations priced both times.</summary>
    public const int MinCompared = 3;

    /// <summary>The figures as of the latest import at or before <paramref name="now"/>; null before the first import.</summary>
    /// <param name="zone">The area's time zone (<c>QuebecTime.ZoneFor</c>): "the same time yesterday" is on its clock.</param>
    public static TrendFigures? Compute(IReadOnlyList<AreaStation> area, Coverage coverage, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (coverage.LatestAtOrBefore(now.UtcTicks) is not { } asOf)
        {
            return null;
        }

        var buffer = new int[area.Count];
        var nowPrices = area.Select(s => s.Prices.TenthsAt(asOf, asOf)).ToArray();
        var seriesFrom = asOf - TimeSpan.TicksPerDay;
        seriesFrom = seriesFrom - seriesFrom % TrendRules.Step.Ticks + TrendRules.Step.Ticks;
        var series = new List<AreaSample?>();
        for (var at = seriesFrom; at <= asOf; at += TrendRules.Step.Ticks)
        {
            series.Add(ReadPoint(coverage, at, asOf) is { } read ? Sample(area, read, asOf, buffer) : null);
        }

        var asOfTime = new DateTimeOffset(asOf, TimeSpan.Zero);
        return new TrendFigures(
            asOfTime,
            Sample(area, asOf, asOf, buffer),
            Compare(area, coverage, nowPrices, SameTimeBefore(asOfTime, zone, 1).UtcTicks, asOf),
            Compare(area, coverage, nowPrices, SameTimeBefore(asOfTime, zone, 7).UtcTicks, asOf),
            Lowest(area, coverage, asOf),
            new DateTimeOffset(seriesFrom, TimeSpan.Zero),
            series);
    }

    /// <summary>
    /// The import a sample at <paramref name="at"/> reads the prices of: the latest up to <see cref="TrendRules.SampleSlack"/>
    /// after it, none after <paramref name="asOf"/>. Null in a gap in the data. The same for every station: found once per sample.
    /// </summary>
    public static long? ReadPoint(Coverage coverage, long at, long asOf) =>
        coverage.IsCovered(at) ? coverage.LatestAtOrBefore(Math.Min(at + TrendRules.SampleSlack.Ticks, asOf)) : null;

    /// <summary>A station's price at a sample moment (<see cref="ReadPoint"/>); null in a gap in the data.</summary>
    public static int? ReadAt(StationPrices prices, Coverage coverage, long at, long asOf) =>
        ReadPoint(coverage, at, asOf) is { } read ? prices.TenthsAt(read, asOf) : null;

    /// <summary>
    /// The same time of day <paramref name="days"/> days before, on the zone's clock; across a change of time where that
    /// time doesn't exist or exists twice, <paramref name="days"/> × 24 hours before.
    /// </summary>
    public static DateTimeOffset SameTimeBefore(DateTimeOffset at, TimeZoneInfo zone, int days)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime.AddDays(-days);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            return at.AddDays(-days);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The area's lowest and median prices at import <paramref name="read"/>; null without any. <paramref name="buffer"/> holds one per station.</summary>
    private static AreaSample? Sample(IReadOnlyList<AreaStation> area, long read, long asOf, int[] buffer)
    {
        var count = 0;
        foreach (var station in area)
        {
            if (station.Prices.TenthsAt(read, asOf) is { } tenths)
            {
                buffer[count++] = tenths;
            }
        }

        if (count == 0)
        {
            return null;
        }

        Array.Sort(buffer, 0, count);
        return new AreaSample(PriceSpan.ToCents(buffer[0]), PriceSpan.ToCents(buffer[(count - 1) / 2]), count);
    }

    /// <summary>The lower of the two middle values with an even count; <paramref name="sorted"/> is sorted.</summary>
    public static int LowerMedian(IReadOnlyList<int> sorted) => sorted[(sorted.Count - 1) / 2];

    private static AreaComparison? Compare(IReadOnlyList<AreaStation> area, Coverage coverage, int?[] nowPrices, long then, long asOf)
    {
        if (ReadPoint(coverage, then, asOf) is not { } read)
        {
            return null;
        }

        var before = new List<int>();
        var after = new List<int>();
        for (var i = 0; i < area.Count; i++)
        {
            if (nowPrices[i] is { } now && area[i].Prices.TenthsAt(read, asOf) is { } earlier)
            {
                before.Add(earlier);
                after.Add(now);
            }
        }

        if (before.Count < MinCompared)
        {
            return null;
        }

        before.Sort();
        after.Sort();
        return new AreaComparison(
            new DateTimeOffset(then, TimeSpan.Zero), PriceSpan.ToCents(LowerMedian(before)), PriceSpan.ToCents(LowerMedian(after)), before.Count);
    }

    /// <summary>
    /// The lowest settled price that was a station's at some covered moment of the last 24 hours; between equal ones, the
    /// latest (still the price first).
    /// </summary>
    private static AreaLowest? Lowest(IReadOnlyList<AreaStation> area, Coverage coverage, long asOf)
    {
        var from = asOf - TimeSpan.TicksPerDay;
        (long Id, PriceSpan Span, long End)? best = null;
        foreach (var station in area)
        {
            var prices = station.Prices;
            for (var i = Math.Max(0, prices.LastStartingAtOrBefore(from)); i < prices.Count; i++)
            {
                var span = prices[i];
                if (span.From > asOf)
                {
                    break;
                }

                var end = Math.Min(span.To, asOf);
                if (span.To <= from || !span.IsAvailable || !span.IsSettled(asOf) || !coverage.AnyCovered(Math.Max(span.From, from), end))
                {
                    continue;
                }

                // Still the price ranks above any that ended.
                var rank = span.To > asOf ? long.MaxValue : end;
                if (best is not { } known || span.Tenths < known.Span.Tenths || (span.Tenths == known.Span.Tenths && rank > known.End))
                {
                    best = (station.Id, span, rank);
                }
            }
        }

        if (best is not { } lowest)
        {
            return null;
        }

        var stillNow = lowest.Span.To > asOf;
        DateTimeOffset? to = stillNow || coverage.IsResume(lowest.Span.To) ? null : new DateTimeOffset(lowest.Span.To, TimeSpan.Zero);
        return new AreaLowest(
            lowest.Id, PriceSpan.ToCents(lowest.Span.Tenths), new DateTimeOffset(Math.Max(lowest.Span.From, from), TimeSpan.Zero), to, stillNow);
    }
}
