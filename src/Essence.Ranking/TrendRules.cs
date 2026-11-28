namespace Essence.Ranking;

/// <summary>
/// The rules of the price trends (<c>/stations/trends</c>, docs/PLAN-price-trends.md). The figures in the comments were
/// measured on 45 days of production history (2026-10-12 to 11-26, Régulier, 10 km areas, simulated every 5 minutes).
/// </summary>
public static class TrendRules
{
    /// <summary>
    /// How far back the history is kept in memory: the weekly test needs 6 weeks with 3 days on each side, and a week more
    /// so that one day with too little data (an outage) doesn't hide the days of the week for 6 weeks.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(56);

    /// <summary>
    /// How long after an import the data still covers the time: the feed comes every 5 minutes, and 5,492 of 5,503 gaps
    /// between imports measured locally were at most 6 minutes. Longer is a gap: a price may have changed unseen.
    /// </summary>
    public static readonly TimeSpan Reach = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A price that lasted less than this doesn't count, unless it's still the price: 3.8 % of the prices last under 20
    /// minutes, often a mistake corrected (a flip-flop).
    /// </summary>
    public static readonly TimeSpan Settled = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A sample at a moment reads the imports up to this after it: imports land a second or two after their slot, and a
    /// sample on the slot would otherwise catch its import or not by chance.
    /// </summary>
    public static readonly TimeSpan SampleSlack = TimeSpan.FromMinutes(2.5);

    /// <summary>The step of the samples: a quarter of an hour, the same in every Québec zone (whole-hour offsets).</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    /// <summary>A station rose when its price is at least this many tenths of a cent above its low before (5 ¢).</summary>
    public const int RiseTenths = 50;

    /// <summary>A station hasn't gone up yet while it's within this many tenths of a cent of its low (1 ¢).</summary>
    public const int HoldTenths = 10;

    /// <summary>
    /// And while its price changed this recently: a station that follows the area's jumps changes its price at each of
    /// them, about every night in Montréal; one unchanged for longer may have a price the feed no longer updates.
    /// </summary>
    public static readonly TimeSpan HeldChangedWithin = TimeSpan.FromDays(2);

    /// <summary>A station's low before a rise is its lowest price in this long before it.</summary>
    public static readonly TimeSpan RiseLookback = TimeSpan.FromHours(4);

    /// <summary>
    /// How far back a jump is looked for: its rises within 3 hours before it started, and their lows 4 hours before those.
    /// Fixed, so a jump's start doesn't move as time passes.
    /// </summary>
    public static readonly TimeSpan Sweep = TimeSpan.FromHours(7);

    /// <summary>Areas with fewer stations get no jump and no pattern: a few stations aren't an area moving.</summary>
    public const int MinStations = 8;

    /// <summary>A jump is at least this share of the area's stations risen within <see cref="RiseWithin"/> (about 70 % do in Montréal).</summary>
    public const double JumpShare = 0.25;

    /// <summary>And at least this many stations, whatever the area's size.</summary>
    public const int JumpMinRaised = 3;

    /// <summary>The rises of one jump all come within this long.</summary>
    public static readonly TimeSpan RiseWithin = TimeSpan.FromHours(3);

    /// <summary>
    /// A jump ends this long after the last rise of a station that usually follows (the chain; of any station where none
    /// is known to, or none of the rises that started it is a follower's): Montréal's last followers raise within 10–15
    /// minutes of the first ones.
    /// </summary>
    public static readonly TimeSpan JumpQuietAfter = TimeSpan.FromMinutes(45);

    /// <summary>A jump lasts this long at most.</summary>
    public static readonly TimeSpan JumpLastsAtMost = TimeSpan.FromHours(3);

    /// <summary>
    /// A station usually follows the area's jumps when it joined this share of them. Followers still at their old price
    /// went up within 3 hours in 95–98 % of the cases in Montréal, Laval and Longueuil; the others, in 52 %.
    /// </summary>
    public const double FollowerShare = 0.6;

    /// <summary>
    /// And it was there for this many of them: in Gatineau, followers judged on 5 irregular jumps went up within 3 hours in
    /// 10 % of the cases only.
    /// </summary>
    public const int FollowerMinJumps = 8;

    /// <summary>The pattern and the followers look at this many days before today.</summary>
    public const int PatternDays = 28;

    /// <summary>Days with enough data the pattern needs.</summary>
    public const int PatternMinDays = 10;

    /// <summary>A day has enough data with this share of its samples.</summary>
    public const double DayCovered = 0.8;

    /// <summary>Jumps come at about the same time on this share of the days (Montréal: 28 of 29).</summary>
    public const double PatternShare = 0.6;

    /// <summary>And on at least this many days.</summary>
    public const int PatternMinEvents = 7;

    /// <summary>A jump is at about the usual time within this much of it, on the clock.</summary>
    public static readonly TimeSpan PatternWithin = TimeSpan.FromMinutes(60);

    /// <summary>A jump within this much of the usual time is the usual one (Laval's come from 23:10 to 00:05).</summary>
    public static readonly TimeSpan UsualWithin = TimeSpan.FromMinutes(90);

    /// <summary>
    /// The time of day prices are lowest is told when the day's swing is at least this many tenths of a cent: 9 ¢ in the
    /// Montréal area, about 1 ¢ elsewhere.
    /// </summary>
    public const int LowSpreadTenths = 30;

    /// <summary>The lowest hours are those within this many tenths of a cent of the lowest (0.5 ¢).</summary>
    public const int LowPlateauTenths = 5;

    /// <summary>And the day's own lowest hour is within <see cref="LowWithinHours"/> of it on this share of the days (86–98 % measured).</summary>
    public const double LowConsistency = 0.6;

    /// <summary>A day's own lowest hour is about the usual one within this many hours of it, on the clock (23 and 1 are 2 apart).</summary>
    public const int LowWithinHours = 2;

    /// <summary>The cheapest days of the week need this many weeks of data.</summary>
    public const int WeeklyMinWeeks = 6;

    /// <summary>
    /// They're told when the two days are at least this many tenths of a cent below the week's average (1.5 ¢): Montréal's
    /// Thursday and Friday were 1.8 and 2.6 ¢ below over 5–6 weeks.
    /// </summary>
    public const int WeeklyBelowTenths = 15;

    /// <summary>And below it on this share of the weeks.</summary>
    public const double WeeklyShare = 0.75;
}
