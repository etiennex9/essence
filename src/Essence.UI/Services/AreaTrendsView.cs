using System.Globalization;
using Essence.Contracts;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.UI.Services;

/// <summary>What Best near you's card says of how prices move around (<see cref="PriceTrends"/>), and which stations haven't gone up yet.</summary>
public sealed class AreaTrendsView(IStringLocalizer<Strings> l)
{
    /// <summary>The card shows with this many stations at least: fewer aren't an area.</summary>
    public const int MinStations = 3;

    /// <summary>Whether the card shows for <paramref name="trends"/>: enough stations within the radius, and prices now.</summary>
    public static bool Shows(PriceTrends? trends) =>
        trends is { RadiusKm: not null, Now: not null } && trends.Stations >= MinStations;

    /// <summary>
    /// A rise is going on: the answer has one and it hasn't ended. It ends at <see cref="PriceJump.Until"/> unless more
    /// stations rise: the next answer says so, and meanwhile the card and tags stop saying it then.
    /// </summary>
    public static bool IsGoingUp(PriceJump? jump, DateTimeOffset now) => jump is not null && now <= jump.Until;

    /// <summary>
    /// The station hasn't gone up yet: a rise is going on, it usually follows them and was still at its price from before,
    /// and its price now (<paramref name="priceCents"/>, perhaps newer than the trends') is still that or lower.
    /// </summary>
    public static bool HasntGoneUp(PriceTrends? trends, long stationId, decimal priceCents, DateTimeOffset now) =>
        trends?.Jump is { } jump
        && IsGoingUp(jump, now)
        && jump.NotRaised.Any(s => s.Id == stationId && priceCents <= s.PriceCents);

    /// <summary>When the tags and the card's rise line go away with no new answer: the rise's end; null without one going on.</summary>
    public static DateTimeOffset? ChangesAt(PriceTrends? trends, DateTimeOffset now) =>
        trends?.Jump is { } jump && IsGoingUp(jump, now) ? jump.Until : null;

    /// <summary>"Prices are going up: 116 of 160 stations up about 11 ¢ since 12:05 a.m."; null when none is going on.</summary>
    public string? JumpText(PriceJump? jump, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (jump is null || !IsGoingUp(jump, now))
        {
            return null;
        }

        var culture = CultureInfo.CurrentCulture;
        return l["TrendsJump", jump.Raised, jump.Stations, Math.Round(jump.RiseCents).ToString("0", culture),
            Formatting.Clock(jump.Since, zone, culture, l["Midnight"])];
    }

    /// <summary>The median's change since then: "+8.0 ¢", "−3.0 ¢", "0.0 ¢"; "—" without a comparison.</summary>
    public static string Change(AreaChange? change) =>
        change is null ? "—" : $"{Formatting.Change(change.NowMedianCents - change.MedianCents, CultureInfo.CurrentCulture)} ¢";

    /// <summary>The figure's tone: up in amber, down in green; none unchanged or without a comparison.</summary>
    public static string? ChangeTone(AreaChange? change) => change is null
        ? null
        : (change.NowMedianCents - change.MedianCents) switch
        {
            > 0 => "is-up",
            < 0 => "is-down",
            _ => null,
        };

    /// <summary>"Lowest in 24 h: 158.9 at Costco, until 9:55 p.m.": still now, until when, or just where.</summary>
    /// <param name="name">The station's name as the pane writes it (its brand's, or "Independent").</param>
    public string LowText(AreaLow low, string name, DateTimeOffset now, TimeZoneInfo zone)
    {
        var culture = CultureInfo.CurrentCulture;
        var price = Formatting.Price(low.PriceCents, culture);
        if (low.StillNow)
        {
            return l["TrendsLowNow", price, name];
        }

        if (low.To is not { } to)
        {
            return l["TrendsLow", price, name];
        }

        var clock = Formatting.Clock(to, zone, culture, l["Midnight"]);
        var sameDay = TimeZoneInfo.ConvertTime(to, zone).Date == TimeZoneInfo.ConvertTime(now, zone).Date;
        return sameDay ? l["TrendsLowUntil", price, name, clock] : l["TrendsLowUntilYesterday", price, name, clock];
    }

    /// <summary>
    /// "Here, prices usually go up around midnight and are lowest in the evening.", with "They’re usually lowest on
    /// Thursdays and Fridays." when the days of the week say it too; null when the pattern says nothing.
    /// </summary>
    public string? PatternText(DailyPattern? pattern)
    {
        if (pattern is null)
        {
            return null;
        }

        var culture = CultureInfo.CurrentCulture;
        var jumps = pattern.JumpsAround is { } around ? Formatting.Clock(around, culture, l["Midnight"]) : null;
        var lowest = pattern is { LowestFromHour: { } from, LowestToHour: { } to } ? (string?)l[DayPartKey(from, to)] : null;
        var days = pattern.CheapestDays is [var first, var second, ..] && IsIsoDay(first) && IsIsoDay(second)
            ? (l[$"Weekday{first}"].Value, l[$"Weekday{second}"].Value)
            : ((string, string)?)null;
        var text = (jumps, lowest) switch
        {
            ({ } j, { } w) => l["TrendsPatternBoth", j, w].Value,
            ({ } j, null) => l["TrendsPatternJump", j].Value,
            (null, { } w) => l["TrendsPatternLowest", w].Value,
            _ => null,
        };
        return (text, days) switch
        {
            ({ } t, { } d) => $"{t} {l["TrendsPatternDays", d.Item1, d.Item2]}",
            ({ } t, null) => t,
            (null, { } d) => l["TrendsPatternDaysOnly", d.Item1, d.Item2],
            _ => null,
        };
    }

    /// <summary>
    /// The part of the day the hours from <paramref name="from"/> to before <paramref name="to"/> (on a clock that goes
    /// round: 18 to 0 is 6 p.m. to midnight) are in, by their middle: night, morning, around noon, afternoon or evening.
    /// </summary>
    public static string DayPartKey(int from, int to)
    {
        var length = (to - from + 24) % 24;
        var middle = (from + (length == 0 ? 24 : length) / 2) % 24;
        return middle switch
        {
            < 5 => "DayPartNight",
            < 11 => "DayPartMorning",
            < 14 => "DayPartMidday",
            < 17 => "DayPartAfternoon",
            _ => "DayPartEvening",
        };
    }

    private static bool IsIsoDay(int day) => day is >= 1 and <= 7;
}
