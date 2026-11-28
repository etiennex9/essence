namespace Essence.Ranking;

/// <summary>How prices usually move in an area, and which of its stations usually follow its jumps.</summary>
/// <param name="JumpsAround">The time of day prices usually go up, to the quarter-hour, on the area's clock; null when not at one time often enough.</param>
/// <param name="LowestFromHour">The first hour (0–23, the area's clock) prices are usually lowest in; null when the day's swing is small or not regular.</param>
/// <param name="LowestToHour">The hour after the last of them (it can be past midnight: 22 to 1).</param>
/// <param name="Days">How many of the last <see cref="TrendRules.PatternDays"/> days had enough data to say.</param>
/// <param name="CheapestDays">The two days of the week prices are usually lowest (ISO: 1 Monday to 7 Sunday, not <see cref="DayOfWeek"/>); null when not clearly.</param>
/// <param name="Weeks">How many weeks of data the days of the week were judged on.</param>
/// <param name="Followers">The stations that joined at least <see cref="TrendRules.FollowerShare"/> of the area's recent jumps.</param>
public sealed record AreaPattern(
    TimeOnly? JumpsAround,
    int? LowestFromHour,
    int? LowestToHour,
    int Days,
    IReadOnlyList<int>? CheapestDays,
    int Weeks,
    StationSet Followers);

/// <summary>Finds an area's usual daily and weekly moves from its history (docs/PLAN-price-trends.md).</summary>
public static class PricePattern
{
    /// <summary>
    /// The area's pattern as of <paramref name="asOf"/>, judged on the complete days before its date; null for an area of
    /// fewer than <see cref="TrendRules.MinStations"/> stations.
    /// </summary>
    public static AreaPattern? Find(IReadOnlyList<AreaStation> area, Coverage coverage, long asOf, TimeZoneInfo zone)
    {
        if (area.Count < TrendRules.MinStations)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(LocalTime(asOf, zone));
        var days = Enumerable.Range(1, TrendRules.PatternDays).Select(i => today.AddDays(-i)).Reverse().ToList();
        var from = Instant(days[0], 0, zone);
        var to = Instant(today, 0, zone);

        var events = JumpEvents(area, coverage, from, to, asOf, out var followers);
        var (jumpsAround, jumpDays) = UsualTime(events, days, coverage, asOf, zone);
        var (lowestFrom, lowestTo, lowDays) = LowestHours(area, coverage, days, asOf, zone);
        var (cheapest, weeks) = CheapestDays(area, coverage, today, asOf, zone);
        return new AreaPattern(jumpsAround, lowestFrom, lowestTo, Math.Max(jumpDays, lowDays), cheapest, weeks, followers);
    }

    /// <summary>
    /// The starts of the area's jumps from <paramref name="from"/> to <paramref name="to"/>, by the rule of
    /// <see cref="Jumps.All"/> on every station's rises (<see cref="Rises.Of"/>), and the stations that joined at least
    /// <see cref="TrendRules.FollowerShare"/> of the jumps they had a price for, <see cref="TrendRules.FollowerMinJumps"/>
    /// at least.
    /// </summary>
    internal static List<long> JumpEvents(IReadOnlyList<AreaStation> area, Coverage coverage, long from, long to, long asOf, out StationSet followers)
    {
        var rises = new List<Rise>();
        for (var s = 0; s < area.Count; s++)
        {
            var station = s;
            rises.AddRange(Rises.Of(area[s].Prices, coverage, from - TrendRules.RiseWithin.Ticks, Math.Min(to + TrendRules.JumpLastsAtMost.Ticks, asOf), asOf)
                .Select(r => new Rise(r.At, r.Low, station)));
        }

        rises.Sort((a, b) => a.At.CompareTo(b.At));
        var priced = new Dictionary<long, int>();
        var jumps = Jumps.All(
            rises, at => priced.TryGetValue(at, out var count) ? count : priced[at] = Jumps.PricedAt(area, at, asOf), followers: null, [.. area.Select(s => s.Id)]);

        var events = new List<long>();
        var joined = new int[area.Count];
        var present = new int[area.Count];
        foreach (var jump in jumps.Where(j => j.Since >= from && j.Since < to))
        {
            events.Add(jump.Since);
            var rose = new HashSet<int>();
            for (var i = jump.First; i <= jump.Last; i++)
            {
                rose.Add(rises[i].Station);
            }

            for (var s = 0; s < area.Count; s++)
            {
                if (area[s].Prices.TenthsAt(jump.Since, asOf) is not null)
                {
                    present[s]++;
                    if (rose.Contains(s))
                    {
                        joined[s]++;
                    }
                }
            }
        }

        followers = StationSet.Of(Enumerable.Range(0, area.Count)
            .Where(s => present[s] >= TrendRules.FollowerMinJumps && joined[s] >= TrendRules.FollowerShare * present[s])
            .Select(s => area[s].Id));
        return events;
    }

    /// <summary>
    /// The time of day the jumps usually come at, when they come within <see cref="TrendRules.PatternWithin"/> of one on at
    /// least <see cref="TrendRules.PatternShare"/> of the days with data around it (and <see cref="TrendRules.PatternMinEvents"/>);
    /// and how many days had data around it.
    /// </summary>
    private static (TimeOnly? Around, int Days) UsualTime(
        List<long> events, List<DateOnly> days, Coverage coverage, long asOf, TimeZoneInfo zone)
    {
        var window = TrendRules.PatternWithin.Ticks;
        (int Minute, int Days, List<long> Matched)? best = null;
        var mostDays = 0;
        for (var minute = 0; minute < 24 * 60; minute += (int)TrendRules.Step.TotalMinutes)
        {
            var qualifying = 0;
            var matched = new List<long>();
            foreach (var day in days)
            {
                var at = Instant(day, minute, zone);
                if (at + TimeSpan.TicksPerHour > asOf
                    || !coverage.FullyCovered(at - TrendRules.RiseWithin.Ticks, at + TimeSpan.TicksPerHour))
                {
                    continue;
                }

                qualifying++;
                var index = events.BinarySearch(at - window);
                index = index < 0 ? ~index : index;
                if (index < events.Count && events[index] <= at + window)
                {
                    matched.Add(events[index]);
                }
            }

            mostDays = Math.Max(mostDays, qualifying);
            if (best is not { } known || matched.Count > known.Matched.Count)
            {
                best = (minute, qualifying, matched);
            }
        }

        if (best is not { } found
            || found.Days < TrendRules.PatternMinDays
            || found.Matched.Count < TrendRules.PatternMinEvents
            || found.Matched.Count < TrendRules.PatternShare * found.Days)
        {
            return (null, best?.Days ?? mostDays);
        }

        // The mean on the clock's circle: 23:55 and 00:05 average to midnight.
        double x = 0, y = 0;
        foreach (var at in found.Matched)
        {
            var local = LocalTime(at, zone);
            var angle = (local.Hour * 60 + local.Minute) / 1440d * 2 * Math.PI;
            x += Math.Cos(angle);
            y += Math.Sin(angle);
        }

        var mean = Math.Atan2(y, x) / (2 * Math.PI) * 1440;
        var step = TrendRules.Step.TotalMinutes;
        var rounded = (int)(Math.Round((mean + 1440) % 1440 / step) * step) % 1440;
        return (new TimeOnly(rounded / 60, rounded % 60), found.Days);
    }

    /// <summary>
    /// The hours prices are usually lowest in: from the area's mean price every quarter-hour, each day's hours against the
    /// day's mean, averaged over the days with enough data. Told when the swing is at least <see cref="TrendRules.LowSpreadTenths"/>
    /// and each day's own lowest hour is within <see cref="TrendRules.LowWithinHours"/> of it on <see cref="TrendRules.LowConsistency"/>
    /// of the days: the hours within <see cref="TrendRules.LowPlateauTenths"/> of the lowest around it.
    /// </summary>
    private static (int? From, int? To, int Days) LowestHours(
        IReadOnlyList<AreaStation> area, Coverage coverage, List<DateOnly> days, long asOf, TimeZoneInfo zone)
    {
        var sums = new double[24];
        var counts = new int[24];
        var dayLows = new List<int>();
        foreach (var day in days)
        {
            var start = Instant(day, 0, zone);
            var end = Instant(day.AddDays(1), 0, zone);
            var expected = (end - start) / TrendRules.Step.Ticks;
            var samples = new List<(int Hour, double Mean)>();
            for (var at = start; at < end; at += TrendRules.Step.Ticks)
            {
                if (MeanAt(area, coverage, at, asOf) is { } mean)
                {
                    samples.Add((LocalTime(at, zone).Hour, mean));
                }
            }

            if (samples.Count < TrendRules.DayCovered * expected)
            {
                continue;
            }

            var dayMean = samples.Average(s => s.Mean);
            var byHour = samples.GroupBy(s => s.Hour).ToDictionary(g => g.Key, g => g.Average(s => s.Mean) - dayMean);
            foreach (var (hour, deviation) in byHour)
            {
                sums[hour] += deviation;
                counts[hour]++;
            }

            dayLows.Add(byHour.MinBy(h => h.Value).Key);
        }

        if (dayLows.Count < TrendRules.PatternMinDays || counts.Any(c => c == 0))
        {
            return (null, null, dayLows.Count);
        }

        var profile = sums.Select((sum, hour) => sum / counts[hour]).ToArray();
        var lowest = Array.IndexOf(profile, profile.Min());
        var consistent = dayLows.Count(h => HoursApart(h, lowest) <= TrendRules.LowWithinHours);
        if (profile.Max() - profile.Min() < TrendRules.LowSpreadTenths || consistent < TrendRules.LowConsistency * dayLows.Count)
        {
            return (null, null, dayLows.Count);
        }

        bool Low(int hour) => profile[(hour + 24) % 24] <= profile[lowest] + TrendRules.LowPlateauTenths;
        int first = lowest, last = lowest;
        for (var i = 1; i < 12 && Low(first - 1); i++)
        {
            first--;
        }

        for (var i = 1; i < 12 && Low(last + 1); i++)
        {
            last++;
        }

        return ((first + 24) % 24, (last + 1 + 24) % 24, dayLows.Count);
    }

    /// <summary>
    /// The two days of the week prices are usually lowest: each day's mean against the 7 days around it, over at least
    /// <see cref="TrendRules.WeeklyMinWeeks"/> weeks, told when the two days are at least <see cref="TrendRules.WeeklyBelowTenths"/>
    /// below on average and below on <see cref="TrendRules.WeeklyShare"/> of the weeks.
    /// </summary>
    private static (IReadOnlyList<int>? Days, int Weeks) CheapestDays(
        IReadOnlyList<AreaStation> area, Coverage coverage, DateOnly today, long asOf, TimeZoneInfo zone)
    {
        if (coverage.First is not { } firstImport)
        {
            return (null, 0);
        }

        var means = new Dictionary<DateOnly, double>();
        for (var day = DateOnly.FromDateTime(LocalTime(firstImport, zone)).AddDays(1); day < today; day = day.AddDays(1))
        {
            var start = Instant(day, 0, zone);
            var end = Instant(day.AddDays(1), 0, zone);
            var samples = new List<double>();
            for (var at = start; at < end; at += TimeSpan.TicksPerHour)
            {
                if (MeanAt(area, coverage, at, asOf) is { } mean)
                {
                    samples.Add(mean);
                }
            }

            if (samples.Count >= TrendRules.DayCovered * ((end - start) / TimeSpan.TicksPerHour))
            {
                means[day] = samples.Average();
            }
        }

        // Against the 7 days around it, one of them missing at most: a day without enough data (an outage) only costs its
        // own deviation, not its week's.
        var deviations = new Dictionary<DateOnly, double>();
        foreach (var (day, mean) in means)
        {
            var around = Enumerable.Range(-3, 7).Select(i => day.AddDays(i)).Where(means.ContainsKey).ToList();
            if (around.Count >= 6)
            {
                deviations[day] = mean - around.Average(d => means[d]);
            }
        }

        (DayOfWeek First, double Mean, double BelowShare, int Weeks)? best = null;
        var mostWeeks = 0;
        foreach (var first in Enum.GetValues<DayOfWeek>())
        {
            var values = deviations
                .Where(d => d.Key.DayOfWeek == first && deviations.ContainsKey(d.Key.AddDays(1)))
                .Select(d => (d.Value + deviations[d.Key.AddDays(1)]) / 2)
                .ToList();
            mostWeeks = Math.Max(mostWeeks, values.Count);
            if (values.Count >= TrendRules.WeeklyMinWeeks && (best is not { } known || values.Average() < known.Mean))
            {
                best = (first, values.Average(), values.Count(v => v < 0) / (double)values.Count, values.Count);
            }
        }

        if (best is not { } found || found.Mean > -TrendRules.WeeklyBelowTenths || found.BelowShare < TrendRules.WeeklyShare)
        {
            return (null, mostWeeks);
        }

        return ([Iso(found.First), Iso((DayOfWeek)(((int)found.First + 1) % 7))], found.Weeks);
    }

    /// <summary>The area's mean price at a sample moment, in tenths of a cent; null with fewer than 3 prices or in a gap.</summary>
    private static double? MeanAt(IReadOnlyList<AreaStation> area, Coverage coverage, long at, long asOf)
    {
        if (Trends.ReadPoint(coverage, at, asOf) is not { } read)
        {
            return null;
        }

        long sum = 0;
        var count = 0;
        foreach (var station in area)
        {
            if (station.Prices.TenthsAt(read, asOf) is { } tenths)
            {
                sum += tenths;
                count++;
            }
        }

        return count >= Trends.MinCompared ? sum / (double)count : null;
    }

    private static int HoursApart(int a, int b) => Math.Min(Math.Abs(a - b), 24 - Math.Abs(a - b));

    /// <summary>ISO 8601's number of the day: 1 Monday to 7 Sunday.</summary>
    private static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    internal static DateTime LocalTime(long ticks, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(ticks, TimeSpan.Zero), zone).DateTime;

    /// <summary>The moment <paramref name="minute"/> minutes after midnight starts <paramref name="day"/> on the zone's clock, in UTC ticks.</summary>
    internal static long Instant(DateOnly day, int minute, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue).AddMinutes(minute);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).UtcTicks;
    }
}
