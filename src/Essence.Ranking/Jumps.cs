namespace Essence.Ranking;

/// <summary>A station that usually follows the area's jumps and hasn't gone up yet, at its price now.</summary>
public readonly record struct HeldPrice(long StationId, decimal PriceCents);

/// <summary>A rise going through the area.</summary>
/// <param name="Since">When it started: the moment enough stations had risen (<see cref="Jumps.Detect"/>).</param>
/// <param name="Until">When it ends unless more stations rise before (<see cref="TrendRules.JumpQuietAfter"/>).</param>
/// <param name="Raised">How many stations rose in it and are still up.</param>
/// <param name="Stations">How many stations of the area have a price.</param>
/// <param name="RiseCents">The median rise of those that rose.</param>
/// <param name="BeforeCents">The median price of those that rose, before.</param>
/// <param name="AfterCents">And now.</param>
/// <param name="NotRaised">The stations that usually follow and are still at their price from before.</param>
public sealed record JumpState(
    DateTimeOffset Since,
    DateTimeOffset Until,
    int Raised,
    int Stations,
    decimal RiseCents,
    decimal BeforeCents,
    decimal AfterCents,
    IReadOnlyList<HeldPrice> NotRaised);

/// <summary>A station's rise: when, and its low of the <see cref="TrendRules.RiseLookback"/> before.</summary>
/// <param name="Station">The station's index in the area.</param>
internal readonly record struct Rise(long At, int Low, int Station);

/// <summary>A jump found among rises: when it started and ends, and its first and last rises' indexes.</summary>
internal readonly record struct FoundJump(long Since, long Until, int First, int Last);

/// <summary>Finds a rise going through the area: many of its stations raising their price at about the same time.</summary>
public static class Jumps
{
    /// <summary>
    /// The rise going on as of <paramref name="asOf"/> (an import), if any, among the rises of the last
    /// <see cref="TrendRules.Sweep"/> (<see cref="Rises.Of"/>) and by the rule of <see cref="All"/>: once over, a late
    /// follower doesn't bring it back, but a new jump can start after it (an evening batch, then the night's jump). Its
    /// stations are those that rose in it and are still up; those that usually follow (<paramref name="followers"/>),
    /// haven't risen, are within <see cref="TrendRules.HoldTenths"/> of their low and changed their price in the last
    /// <see cref="TrendRules.HeldChangedWithin"/> haven't gone up yet. A station whose price changed at an import that came
    /// after a gap is neither: its change may have happened any time during the gap. Null when there's none, or the area
    /// has fewer than <see cref="TrendRules.MinStations"/> stations with a price.
    /// </summary>
    public static JumpState? Detect(IReadOnlyList<AreaStation> area, Coverage coverage, long asOf, StationSet? followers)
    {
        var sweepFrom = asOf - TrendRules.Sweep.Ticks;
        var rises = new List<Rise>();
        var now = new int?[area.Count];
        var touched = new bool[area.Count];
        for (var s = 0; s < area.Count; s++)
        {
            var prices = area[s].Prices;
            now[s] = prices.TenthsAt(asOf, asOf);
            rises.AddRange(Rises.Of(prices, coverage, sweepFrom, asOf, asOf).Select(r => new Rise(r.At, r.Low, s)));
            touched[s] = Rises.Settled(prices, sweepFrom, asOf, asOf).Skip(1).Any(c => coverage.IsResume(c.At));
        }

        var priced = now.Count(p => p is not null);
        if (priced < TrendRules.MinStations)
        {
            return null;
        }

        rises.Sort((a, b) => a.At.CompareTo(b.At));
        var ids = area.Select(s => s.Id).ToList();
        var jumps = All(rises, at => PricedAt(area, at, asOf), followers, ids);
        if (jumps.Count == 0 || asOf > jumps[^1].Until)
        {
            return null;
        }

        var jump = jumps[^1];

        // Each station once, with its first rise of the jump, if it's still up.
        var raised = new List<(int Station, int Low, int Now)>();
        var rose = new HashSet<int>();
        for (var i = jump.First; i <= jump.Last; i++)
        {
            var rise = rises[i];
            if (rose.Add(rise.Station) && !touched[rise.Station] && now[rise.Station] is { } price && price >= rise.Low + TrendRules.RiseTenths)
            {
                raised.Add((rise.Station, rise.Low, price));
            }
        }

        if (raised.Count == 0)
        {
            return null;
        }

        var before = Median(raised.Select(r => r.Low));
        var after = Median(raised.Select(r => r.Now));
        var held = new List<(int Station, int Now)>();
        if (followers is not null)
        {
            for (var s = 0; s < area.Count; s++)
            {
                if (now[s] is not { } price || touched[s] || rose.Contains(s) || !followers.Contains(area[s].Id))
                {
                    continue;
                }

                var prices = area[s].Prices;
                var changes = Rises.Settled(prices, asOf - TrendRules.RiseLookback.Ticks, asOf, asOf);
                var changedAt = prices.SettledAt(asOf, asOf) is var index and >= 0 ? prices[index].From : long.MinValue;
                if (Rises.LowSince(changes, asOf - TrendRules.RiseLookback.Ticks) is { } low
                    && price - low <= TrendRules.HoldTenths
                    && 2 * price <= before + after
                    && asOf - changedAt <= TrendRules.HeldChangedWithin.Ticks)
                {
                    held.Add((s, price));
                }
            }
        }

        return new JumpState(
            new DateTimeOffset(jump.Since, TimeSpan.Zero),
            new DateTimeOffset(jump.Until, TimeSpan.Zero),
            raised.Count,
            priced,
            PriceSpan.ToCents(Median(raised.Select(r => r.Now - r.Low))),
            PriceSpan.ToCents(before),
            PriceSpan.ToCents(after),
            [.. held.OrderBy(h => h.Now).Select(h => new HeldPrice(area[h.Station].Id, PriceSpan.ToCents(h.Now)))]);
    }

    /// <summary>
    /// The jumps among <paramref name="rises"/> (sorted by time), one after the other. One starts when
    /// <see cref="TrendRules.JumpShare"/> of the stations with a price then (<paramref name="pricedAt"/>; at least
    /// <see cref="TrendRules.JumpMinRaised"/>, and <see cref="TrendRules.MinStations"/> with a price) rose within
    /// <see cref="TrendRules.RiseWithin"/>, at the last rise needed. It ends <see cref="TrendRules.JumpQuietAfter"/> after
    /// its last rise of a station that usually follows (<paramref name="followers"/>, by <paramref name="ids"/>; of any
    /// station when none is known, or none of the rises that started it is a follower's), each counting only while it's
    /// still going, and <see cref="TrendRules.JumpLastsAtMost"/> after it started at most. The next starts after it ends.
    /// </summary>
    internal static List<FoundJump> All(List<Rise> rises, Func<long, int> pricedAt, StationSet? followers, IReadOnlyList<long> ids)
    {
        var within = TrendRules.RiseWithin.Ticks;
        var quiet = TrendRules.JumpQuietAfter.Ticks;
        var jumps = new List<FoundJump>();
        var inWindow = new Dictionary<int, int>();
        var start = 0;
        for (var j = 0; j < rises.Count; j++)
        {
            var since = rises[j].At;
            while (rises[start].At < since - within)
            {
                if (--inWindow[rises[start].Station] == 0)
                {
                    inWindow.Remove(rises[start].Station);
                }

                start++;
            }

            inWindow[rises[j].Station] = inWindow.GetValueOrDefault(rises[j].Station) + 1;
            if (inWindow.Count < TrendRules.JumpMinRaised)
            {
                continue;
            }

            var priced = pricedAt(since);
            if (priced < TrendRules.MinStations || inWindow.Count < Math.Max(TrendRules.JumpMinRaised, (int)Math.Ceiling(TrendRules.JumpShare * priced)))
            {
                continue;
            }

            var byFollowers = followers is { Count: > 0 } && Enumerable.Range(start, j - start + 1).Any(i => followers.Contains(ids[rises[i].Station]));
            var until = since + quiet;
            var last = j;
            for (var i = start; i < rises.Count && rises[i].At <= until; i++)
            {
                if (!byFollowers || followers!.Contains(ids[rises[i].Station]))
                {
                    until = Math.Max(until, rises[i].At + quiet);
                }
            }

            until = Math.Min(until, since + TrendRules.JumpLastsAtMost.Ticks);
            while (last + 1 < rises.Count && rises[last + 1].At <= until)
            {
                last++;
            }

            jumps.Add(new FoundJump(since, until, start, last));

            // The next jump is made of the rises after this one.
            j = last;
            start = last + 1;
            inWindow.Clear();
        }

        return jumps;
    }

    /// <summary>How many of the area's stations had a price at <paramref name="at"/>, as of <paramref name="asOf"/>.</summary>
    internal static int PricedAt(IReadOnlyList<AreaStation> area, long at, long asOf)
    {
        var count = 0;
        foreach (var station in area)
        {
            if (station.Prices.TenthsAt(at, asOf) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static int Median(IEnumerable<int> values) => Trends.LowerMedian(values.Order().ToList());
}

/// <summary>Reads a station's price changes and finds its rises.</summary>
internal static class Rises
{
    /// <summary>
    /// The settled prices (as of <paramref name="asOf"/>) in effect from <paramref name="from"/> to <paramref name="to"/>:
    /// the one at <paramref name="from"/> first, then each change, in tenths of a cent (-1 unavailable). A flip-flop isn't
    /// a change.
    /// </summary>
    public static List<(long At, int Tenths)> Settled(StationPrices prices, long from, long to, long asOf)
    {
        var changes = new List<(long At, int Tenths)>();
        var first = prices.SettledAt(from, asOf);
        changes.Add((from, first >= 0 ? prices[first].Tenths : PriceSpan.Unavailable));
        for (var i = prices.LastStartingAtOrBefore(from) + 1; i < prices.Count && prices[i].From <= to; i++)
        {
            if (prices[i].IsSettled(asOf) && prices[i].Tenths != changes[^1].Tenths)
            {
                changes.Add((prices[i].From, prices[i].Tenths));
            }
        }

        return changes;
    }

    /// <summary>The lowest price in the <see cref="TrendRules.RiseLookback"/> before change <paramref name="k"/>; null without one.</summary>
    public static int? LowBefore(List<(long At, int Tenths)> changes, int k)
    {
        var from = changes[k].At - TrendRules.RiseLookback.Ticks;
        int? low = null;
        for (var j = k - 1; j >= 0; j--)
        {
            if (changes[j].Tenths >= 0 && !(low <= changes[j].Tenths))
            {
                low = changes[j].Tenths;
            }

            // Change j started its price: the one before held until it, so it's in the lookback only if j is after its start.
            if (changes[j].At <= from)
            {
                break;
            }
        }

        return low;
    }

    /// <summary>The lowest price from <paramref name="from"/> on; null without one.</summary>
    public static int? LowSince(List<(long At, int Tenths)> changes, long from)
    {
        int? low = null;
        for (var j = changes.Count - 1; j >= 0; j--)
        {
            if (changes[j].Tenths >= 0 && !(low <= changes[j].Tenths))
            {
                low = changes[j].Tenths;
            }

            if (changes[j].At <= from)
            {
                break;
            }
        }

        return low;
    }

    /// <summary>
    /// A station's rises after <paramref name="from"/> and up to <paramref name="to"/>: changes up from its price before,
    /// to at least <see cref="TrendRules.RiseTenths"/> above its low of the <see cref="TrendRules.RiseLookback"/> before,
    /// not at an import after a gap, and <see cref="TrendRules.RiseWithin"/> apart at least (a jump in two steps is one
    /// rise). Each with that low.
    /// </summary>
    public static List<(long At, int Low)> Of(StationPrices prices, Coverage coverage, long from, long to, long asOf)
    {
        var changes = Settled(prices, from - TrendRules.RiseLookback.Ticks, to, asOf);
        var rises = new List<(long At, int Low)>();
        for (var k = 1; k < changes.Count; k++)
        {
            var (at, tenths) = changes[k];
            if (at <= from
                || tenths < 0
                || tenths <= changes[k - 1].Tenths
                || coverage.IsResume(at)
                || (rises.Count > 0 && at - rises[^1].At < TrendRules.RiseWithin.Ticks))
            {
                continue;
            }

            if (LowBefore(changes, k) is { } low && tenths >= low + TrendRules.RiseTenths)
            {
                rises.Add((at, low));
            }
        }

        return rises;
    }
}
