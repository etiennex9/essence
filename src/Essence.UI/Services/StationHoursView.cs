using System.Collections.Concurrent;
using System.Globalization;
using Essence.Contracts;
using Essence.Hours;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.UI.Services;

/// <summary>Whether a station is open, as the app says it.</summary>
public enum HoursKind
{
    /// <summary>No source has its hours, or the API sent a rule this app can't read.</summary>
    Unknown,

    Open,

    /// <summary>Open, and it closes within <see cref="StationHoursView.ClosesSoonWithin"/>.</summary>
    ClosesSoon,

    Closed,

    /// <summary>Open 24/7, holidays included.</summary>
    AlwaysOpen,

    /// <summary>The source says it's closed for now, whatever its hours.</summary>
    TemporarilyClosed,
}

/// <summary>A line of a station's week: days in a row with the same hours, or its public holidays'.</summary>
/// <param name="Days">"Mon–Fri", "Sat", "Holidays".</param>
/// <param name="Hours">"6:00 a.m. – 9:30 p.m.", "Closed", "Open 24 hours".</param>
/// <param name="Today">The station's day is in it: today's line, or the holidays' on one.</param>
public sealed record HoursLine(string Days, string Hours, bool Today);

/// <summary>A station's hours as the app shows them, at a moment: texts ready to show, in the station's time zone.</summary>
/// <param name="ChangesAt">When it closes if it's open, or opens if it's closed; null when that's not known or never.</param>
/// <param name="ClosesAt">When it closes, while it's open.</param>
/// <param name="HolidayCaution">Today is a holiday, or a day stores' hours are restricted, and its hours may differ.</param>
/// <param name="Status">The details' line: "Open until 9:30 p.m.", "Closed · opens 6:00 a.m.", "Open 24 hours"…; null when unknown.</param>
/// <param name="Closes">"Closes 9:30 p.m.", while it's open and when it closes is known.</param>
/// <param name="Tag">A row's word, in the favorites and the comparison: "Closed", "Temporarily closed", or <paramref name="Closes"/> when it closes soon.</param>
/// <param name="Source">Where the hours come from: "Costco", "Checked by Essence, November 11"…</param>
/// <param name="SourceLink">Where a person can check them: a web address, or null.</param>
public sealed record HoursView(
    HoursKind Kind,
    DateTimeOffset? ChangesAt,
    DateTimeOffset? ClosesAt,
    bool HolidayCaution,
    string? Status,
    string? Closes,
    string? Tag,
    IReadOnlyList<HoursLine> Week,
    string? Source,
    string? SourceLink)
{
    public static HoursView Unknown { get; } = new(HoursKind.Unknown, null, null, false, null, null, null, [], null, null);

    public bool IsClosed => Kind is HoursKind.Closed or HoursKind.TemporarilyClosed;

    /// <summary>When what's shown changes next: it starts closing soon, closes or opens.</summary>
    public DateTimeOffset? RefreshAt =>
        Kind == HoursKind.Open && ClosesAt is { } closes ? closes - StationHoursView.ClosesSoonWithin : ChangesAt;
}

/// <summary>What the driver finds on getting to a station, as the app says it.</summary>
public enum ArrivalKind
{
    /// <summary>No hours, or a rule this app can't read: nothing is said.</summary>
    Unknown,

    /// <summary>Open, and not closing soon after (or open 24/7).</summary>
    Open,

    /// <summary>Open, but it closes within <see cref="StationHoursView.ClosesSoonWithin"/> after the arrival.</summary>
    ClosesSoon,

    /// <summary>Open now, but it closes before the driver gets there.</summary>
    ClosesBefore,

    /// <summary>Closed now, and still closed when the driver gets there.</summary>
    Closed,

    /// <summary>The source says it's closed for now, whatever its hours.</summary>
    TemporarilyClosed,
}

/// <summary>
/// A station's hours at the driver's arrival (<see cref="StationHoursView.Arrive"/>), with the texts Best near you, Best on
/// the way, their rows and the Go links say of them, ready to show in the UI's language and the station's time zone.
/// </summary>
/// <param name="ArriveAt">When the driver gets there: now plus how long getting there takes.</param>
/// <param name="ClosesAt">When it closes: after the arrival when it's open then, before it with <see cref="ArrivalKind.ClosesBefore"/>.</param>
/// <param name="OpensAt">When it next opens after the arrival, when it's <see cref="ArrivalKind.Closed"/>.</param>
/// <param name="Suffix">The hero's address line's ending, closing soon: "Closes 9:30 p.m.".</param>
/// <param name="Why">What the hero's explanation starts with, closing soon: "Closes at 9:30 p.m., about 8 min after you get there."</param>
/// <param name="Blocked">The hero's headline instead of its saving, closed then: "Closed by the time you get there (closes 9:30 p.m.)."</param>
/// <param name="Tag">A row's or a tile's tag: "Closes 9:30 p.m.", "Closed", "Temporarily closed".</param>
/// <param name="DialogTitle">The Go link's warning (ClosingDialog): "Costco closes at 9:30 p.m."; null when it goes straight to Google Maps.</param>
/// <param name="DialogLines">What the warning says under its title: "You’d get there around 9:22 p.m."…</param>
public sealed record ArrivalView(
    ArrivalKind Kind,
    DateTimeOffset ArriveAt,
    DateTimeOffset? ClosesAt,
    DateTimeOffset? OpensAt,
    string? Suffix,
    string? Why,
    string? Blocked,
    string? Tag,
    string? DialogTitle,
    IReadOnlyList<string> DialogLines)
{
    public static ArrivalView Unknown(DateTimeOffset arriveAt) => new(ArrivalKind.Unknown, arriveAt, null, null, null, null, null, null, null, []);

    /// <summary>It closes soon after the arrival or is closed then: the Go link asks first.</summary>
    public bool Warns => DialogTitle is not null;

    /// <summary>
    /// What a screen reader hears when the station is chosen (BestPanel's role="status" line): "Shown above: Costco. Saves
    /// $3.10. Closes at 9:30 p.m., about 8 min after you get there."; closed then, the headline it says is
    /// <see cref="Blocked"/>.
    /// </summary>
    /// <param name="shown">"Shown above: Costco."</param>
    /// <param name="hero">The hero's texts without the arrival (<see cref="Apply"/>).</param>
    public string Announcement(string shown, (string? Saves, string? Tone, string? Why) hero) =>
        string.Join(" ", new[] { shown, Apply(hero).Saves is { } saves ? Sentence(saves) : null, Why }.OfType<string>());

    /// <summary>
    /// When what's said of it changes next as time goes by, the time there staying the same: it starts closing soon, closes
    /// before the arrival, or opens by then; closing soon, every minute too ("about 8 min after"). Null when it can't change
    /// (unknown hours, open 24/7, closed for now).
    /// </summary>
    public DateTimeOffset? RefreshAt(DateTimeOffset now)
    {
        // Thresholds on the arrival, moved back to now: the arrival is now plus the same time there.
        DateTimeOffset? OnArrival(DateTimeOffset? at) => at is { } moment ? now + (moment - ArriveAt) : null;
        return Kind switch
        {
            ArrivalKind.Open => OnArrival(ClosesAt - StationHoursView.ClosesSoonWithin),
            ArrivalKind.ClosesSoon => Earliest(OnArrival(ClosesAt), now + TimeSpan.FromMinutes(1)),
            ArrivalKind.ClosesBefore => Earliest(ClosesAt, OnArrival(OpensAt)),
            ArrivalKind.Closed => OnArrival(OpensAt),
            _ => null,
        };
    }

    /// <summary>The earliest of the moments given, ignoring nulls.</summary>
    public static DateTimeOffset? Earliest(params DateTimeOffset?[] moments)
    {
        DateTimeOffset? earliest = null;
        foreach (var moment in moments)
        {
            if (moment is { } m && (earliest is not { } e || m < e))
            {
                earliest = m;
            }
        }

        return earliest;
    }

    /// <summary>The text ending with a period, once.</summary>
    private static string Sentence(string text) => text.EndsWith('.') ? text : $"{text}.";

    /// <summary>
    /// The hero's headline, its tone and its explanation (BestPanel's), with the arrival: closing soon, the explanation
    /// starts with it; closed then, the headline says so instead of the saving, in amber (<c>is-blocked</c>).
    /// </summary>
    public (string? Saves, string? Tone, string? Why) Apply((string? Saves, string? Tone, string? Why) hero) => (
        Blocked ?? hero.Saves,
        Blocked is not null ? "is-blocked" : hero.Tone,
        Why is null ? hero.Why : hero.Why is null ? Why : $"{Why} {hero.Why}");
}

/// <summary>
/// A station's hours from the API (<see cref="StationHours"/>), against the clock: open, closing soon or closed now, or when
/// the driver gets there (<see cref="Arrive"/>), its week, and where the hours come from, in the UI's language. Always in the
/// station's time zone (<see cref="QuebecTime.ZoneFor"/>): a phone set to another zone still sees Laval's 9:30 p.m. Pure: the
/// moment is given.
/// </summary>
public sealed class StationHoursView(IStringLocalizer<Strings> l)
{
    /// <summary>Closing within this is closing soon.</summary>
    public static readonly TimeSpan ClosesSoonWithin = TimeSpan.FromMinutes(30);

    /// <summary>The rules read so far, by text: few, and the same for every station of a brand. Emptied past this many.</summary>
    private const int MaxRules = 512;

    /// <summary>How the sources' brands are named; a source this doesn't know is shown as the API names it.</summary>
    private static readonly Dictionary<string, string> Brands = new(StringComparer.Ordinal)
    {
        ["costco"] = "Costco",
        ["circlek"] = "Couche-Tard",
        ["ultramar"] = "Ultramar",
        ["shell"] = "Shell",
    };

    private static readonly ConcurrentDictionary<string, OpeningHours?> Rules = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the station is open at <paramref name="now"/>, and when that changes, without the texts: for many stations at
    /// once, the map's.
    /// </summary>
    public static (HoursKind Kind, DateTimeOffset? ChangesAt) StateAt(StationHours? hours, double? latitude, double? longitude, DateTimeOffset now)
    {
        if (hours is null || Read(hours.Rule) is not { } rule)
        {
            return (HoursKind.Unknown, null);
        }

        if (hours.TemporarilyClosed)
        {
            return (HoursKind.TemporarilyClosed, null);
        }

        if (rule.IsAlwaysOpen)
        {
            return (HoursKind.AlwaysOpen, null);
        }

        var check = HoursEvaluator.OnArrival(rule, QuebecTime.ZoneFor(latitude, longitude), now, ClosesSoonWithin);
        return check.State switch
        {
            Arrival.Closed => (HoursKind.Closed, check.OpensAt),
            Arrival.ClosesSoon => (HoursKind.ClosesSoon, check.ClosesAt),
            _ => (HoursKind.Open, check.ClosesAt),
        };
    }

    /// <summary>
    /// Whether the map draws the station closed at <paramref name="now"/> (its hours say it's closed, or its source that it's
    /// closed for now), and when that changes: when it opens or closes, or null when it won't within the days looked at.
    /// </summary>
    public static (bool Closed, DateTimeOffset? ChangesAt) MarkerAt(StationHours? hours, double? latitude, double? longitude, DateTimeOffset now)
    {
        var (kind, changesAt) = StateAt(hours, latitude, longitude, now);
        return (kind is HoursKind.Closed or HoursKind.TemporarilyClosed, changesAt);
    }

    /// <summary>The station's hours at <paramref name="now"/>, in the current culture.</summary>
    public HoursView Describe(StationHours? hours, double? latitude, double? longitude, DateTimeOffset now)
    {
        if (hours is null || Read(hours.Rule) is not { } rule)
        {
            return HoursView.Unknown;
        }

        var culture = CultureInfo.CurrentCulture;
        var zone = QuebecTime.ZoneFor(latitude, longitude);
        var week = Week(rule, zone, now, culture);
        var source = SourceOf(hours, zone, now, culture);
        var link = Uri.TryCreate(hours.Link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? hours.Link : null;
        if (hours.TemporarilyClosed)
        {
            string closed = l["TemporarilyClosed"];
            return new HoursView(HoursKind.TemporarilyClosed, null, null, false, closed, null, closed, week, source, link);
        }

        if (rule.IsAlwaysOpen)
        {
            return new HoursView(HoursKind.AlwaysOpen, null, null, false, l["Open24h"], null, null, week, source, link);
        }

        var check = HoursEvaluator.OnArrival(rule, zone, now, ClosesSoonWithin);
        if (check.State == Arrival.Closed)
        {
            string status = check.OpensAt is { } opens
                ? Soon(opens, now) ? l["ClosedOpensAt", Clock(opens, zone, culture)] : l["ClosedOpensDayAt", Day(opens, zone, culture), Clock(opens, zone, culture)]
                : l["HoursClosedDay"];
            return new HoursView(HoursKind.Closed, check.OpensAt, null, check.HolidayCaution, status, null, l["HoursClosedDay"], week, source, link);
        }

        var kind = check.State == Arrival.ClosesSoon ? HoursKind.ClosesSoon : HoursKind.Open;
        if (check.ClosesAt is not { } closesAt)
        {
            return new HoursView(kind, null, null, check.HolidayCaution, l["OpenNow"], null, null, week, source, link);
        }

        var time = Clock(closesAt, zone, culture);
        string until = Soon(closesAt, now) ? l["OpenUntil", time] : l["OpenUntilDay", Day(closesAt, zone, culture), time];
        string closes = l["ClosesAt", time];
        return new HoursView(
            kind, closesAt, closesAt, check.HolidayCaution, until, closes, kind == HoursKind.ClosesSoon ? closes : null, week, source, link);
    }

    /// <summary>
    /// How long driving a straight-line distance takes, as the API estimates it when it can't route (Essence.Ranking's
    /// <c>NearYou.Estimate</c>): roads 1.3 times longer, driven at 40 km/h. For the details' Go link, when they weren't
    /// opened from Best near you or the trip, which know it by road.
    /// </summary>
    public static double EstimatedMinutes(double straightKm) => straightKm * EstimatedRoadFactor / EstimatedSpeedKmh * 60;

    private const double EstimatedRoadFactor = 1.3;
    private const double EstimatedSpeedKmh = 40;

    /// <summary>
    /// The station's hours when the driver gets there, <paramref name="etaMinutes"/> after <paramref name="now"/>, in the
    /// current culture: open, closing soon after (within <see cref="ClosesSoonWithin"/>, the owner's warning), closed
    /// by then, or closed now and still then. Unknown hours say nothing; a holiday's caution isn't part of it (the details
    /// say it).
    /// </summary>
    /// <param name="etaMinutes">How long getting there takes; null when it isn't known, and the arrival is now.</param>
    /// <param name="name">The station's name in the Go link's warning: its brand, or "Independent".</param>
    public ArrivalView Arrive(StationHours? hours, double? latitude, double? longitude, DateTimeOffset now, double? etaMinutes, string name)
    {
        var arriveAt = now.AddMinutes(Math.Max(0, etaMinutes ?? 0));
        if (hours is null || Read(hours.Rule) is not { } rule)
        {
            return ArrivalView.Unknown(arriveAt);
        }

        var culture = CultureInfo.CurrentCulture;
        var zone = QuebecTime.ZoneFor(latitude, longitude);
        string? arrive = etaMinutes is null ? null : l["ClosingDialogArrive", Clock(arriveAt, zone, culture)].Value;
        // "around 9:20 p.m.." ends once: a time can end with a period, and so does the sentence.
        IReadOnlyList<string> Lines(params string?[] lines) =>
            [.. lines.OfType<string>().Select(line => line.EndsWith("..", StringComparison.Ordinal) ? line[..^1] : line)];
        if (hours.TemporarilyClosed)
        {
            string closed = l["TemporarilyClosed"];
            return new ArrivalView(
                ArrivalKind.TemporarilyClosed, arriveAt, null, null, null, null, closed, closed, l["ClosingDialogClosed", name],
                Lines(l["ClosingDialogTemporarily"]));
        }

        var check = HoursEvaluator.OnArrival(rule, zone, arriveAt, ClosesSoonWithin);
        switch (check.State)
        {
            case Arrival.Open:
                return new ArrivalView(ArrivalKind.Open, arriveAt, check.ClosesAt, null, null, null, null, null, null, []);

            case Arrival.ClosesSoon when check.ClosesAt is { } closesAt:
            {
                var time = Clock(closesAt, zone, culture);
                string closes = l["ClosesAt", time];
                var minutes = Math.Max(1, (int)Math.Round((closesAt - arriveAt).TotalMinutes));
                return new ArrivalView(
                    ArrivalKind.ClosesSoon, arriveAt, closesAt, null, closes, l["ClosesSoonAfterArrival", time, minutes], null, closes,
                    l["ClosingDialogTitle", name, time], Lines(arrive));
            }

            case Arrival.Closed when HoursEvaluator.At(rule, zone, now) is { IsOpen: true, ChangesAt: { } closedAt }:
            {
                // Open now: it closes on the way.
                var time = Clock(closedAt, zone, culture);
                return new ArrivalView(
                    ArrivalKind.ClosesBefore, arriveAt, closedAt, check.OpensAt, null, null, l["ClosedOnArrival", time], l["HoursClosedDay"],
                    l["ClosingDialogTitle", name, time], Lines(arrive));
            }

            case Arrival.Closed:
            {
                var opens = check.OpensAt is { } opensAt ? Opens(opensAt, now, zone, culture) : null;
                return new ArrivalView(
                    ArrivalKind.Closed, arriveAt, null, check.OpensAt, null, null,
                    opens is null ? l["HoursClosedDay"] : l["ClosedOnArrivalOpens", opens], l["HoursClosedDay"],
                    l["ClosingDialogClosed", name], Lines(arrive, opens is null ? null : l["ClosingDialogOpens", opens].Value));
            }

            default:
                // Closing soon at a time past the days laid out: it can't be.
                return new ArrivalView(ArrivalKind.Open, arriveAt, check.ClosesAt, null, null, null, null, null, null, []);
        }
    }

    /// <summary>
    /// Why Best near you or Best on the way left a station out (<see cref="LeftOutStation"/>), at <paramref name="now"/>:
    /// "Costco left out: closed by the time you’d get there (closes 9:30 p.m.)." Null when its hours no longer say it's
    /// closed then (it opened since the answer): the app doesn't contradict itself.
    /// </summary>
    public string? LeftOut(LeftOutStation station, string name, DateTimeOffset now)
    {
        var arrival = Arrive(station.Hours, station.Latitude, station.Longitude, now, station.EtaMin, name);
        var zone = QuebecTime.ZoneFor(station.Latitude, station.Longitude);
        var culture = CultureInfo.CurrentCulture;
        return arrival switch
        {
            { Kind: ArrivalKind.TemporarilyClosed } => l["LeftOutTemporarilyClosed", name].Value,
            { Kind: ArrivalKind.ClosesBefore, ClosesAt: { } closesAt } => l["LeftOutClosed", name, Clock(closesAt, zone, culture)].Value,
            { Kind: ArrivalKind.Closed, OpensAt: { } opensAt } => l["LeftOutClosedOpens", name, Opens(opensAt, now, zone, culture)].Value,
            { Kind: ArrivalKind.Closed } => l["LeftOutClosedNow", name].Value,
            _ => null,
        };
    }

    /// <summary>"6:00 a.m." or "Mon 7:00 a.m." (fr "à 6 h", "lun. à 7 h"): when it opens, its day once it's a day away.</summary>
    private string Opens(DateTimeOffset opensAt, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture) =>
        Soon(opensAt, now)
            ? l["OpensTime", Clock(opensAt, zone, culture)]
            : l["OpensDayTime", Day(opensAt, zone, culture), Clock(opensAt, zone, culture)];

    /// <summary>The rule read, or null when it can't be: a newer API's, say. Read once per text.</summary>
    private static OpeningHours? Read(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return null;
        }

        if (Rules.TryGetValue(rule, out var read))
        {
            return read;
        }

        if (Rules.Count >= MaxRules)
        {
            Rules.Clear();
        }

        return Rules[rule] = OpeningHours.TryParse(rule, lenient: false, out var hours, out _) ? hours : null;
    }

    /// <summary>Within a day: the time alone says which day, the next one's if it's earlier than now.</summary>
    private static bool Soon(DateTimeOffset at, DateTimeOffset now) => at - now < TimeSpan.FromDays(1);

    /// <summary>The week, Monday first, then the holidays when the rule gives their hours; today's line flagged.</summary>
    private List<HoursLine> Week(OpeningHours rule, TimeZoneInfo zone, DateTimeOffset now, CultureInfo culture)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var holiday = rule.OnHolidays is not null && QuebecHolidays.IsPublicHoliday(today);
        var names = culture.DateTimeFormat.AbbreviatedDayNames;
        var lines = rule.Groups()
            .Select(g => new HoursLine(
                g.First == g.Last ? names[(int)g.First] : $"{names[(int)g.First]}–{names[(int)g.Last]}",
                Ranges(g.Ranges, culture),
                !holiday && Contains(g, today.DayOfWeek)))
            .ToList();
        if (rule.OnHolidays is { } holidays)
        {
            lines.Add(new HoursLine(l["HoursHolidays"], Ranges(holidays, culture), holiday));
        }

        return lines;
    }

    /// <summary>The week's groups run Monday to Sunday.</summary>
    private static bool Contains(DayGroup group, DayOfWeek day)
    {
        static int FromMonday(DayOfWeek d) => ((int)d + 6) % 7;
        return FromMonday(day) >= FromMonday(group.First) && FromMonday(day) <= FromMonday(group.Last);
    }

    /// <summary>"6:00 a.m. – 9:30 p.m.", "Closed", "Open 24 hours".</summary>
    private string Ranges(IReadOnlyList<TimeRange> ranges, CultureInfo culture) =>
        ranges.Count == 0
            ? l["HoursClosedDay"]
            : string.Join(", ", ranges.Select(r => r == TimeRange.AllDay
                ? l["Open24h"].Value
                : $"{Clock(r.StartMinute, culture)} – {Clock(r.EndMinute, culture)}"));

    private string Clock(DateTimeOffset at, TimeZoneInfo zone, CultureInfo culture) => Formatting.Clock(at, zone, culture, l["Midnight"]);

    /// <summary>A minute of the day, or past midnight for a range that spills into the next.</summary>
    private string Clock(int minute, CultureInfo culture) =>
        Formatting.Clock(new TimeOnly(minute % TimeRange.MinutesPerDay / 60, minute % 60), culture, l["Midnight"]);

    /// <summary>
    /// "Thu", "jeu.", in the station's zone. 00:00 is the day before's: the clock calls it midnight, the end of a day, so a
    /// station open around the clock on weekdays is open until "Fri midnight", not "Sat midnight".
    /// </summary>
    private static string Day(DateTimeOffset at, TimeZoneInfo zone, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var day = local.TimeOfDay == TimeSpan.Zero ? local.AddDays(-1).DayOfWeek : local.DayOfWeek;
        return culture.DateTimeFormat.AbbreviatedDayNames[(int)day];
    }

    /// <summary>
    /// "Costco", "Checked by Essence, November 11", "OpenStreetMap, checked 2025": a brand's name, the day Essence checked
    /// its rule (its month and year when it's not this year), or the year a mapper last checked OpenStreetMap's hours
    /// (<c>check_date:opening_hours</c>, else <c>check_date</c>).
    /// </summary>
    private string SourceOf(StationHours hours, TimeZoneInfo zone, DateTimeOffset now, CultureInfo culture) => hours.Source switch
    {
        "curated" when hours.CheckedAt is { } at => l["HoursFromCurated", Year(at, zone) == Year(now, zone)
            ? Formatting.MonthDay(at, zone, culture)
            : Formatting.MonthYear(at, zone, culture)],
        "curated" => l["HoursFromEssence"],
        "osm" when hours.CheckedAt is { } at => l["HoursFromOsm", Year(at, zone).ToString(culture)],
        "osm" => l["HoursFromBrand", "OpenStreetMap"],
        _ => l["HoursFromBrand", Brands.GetValueOrDefault(hours.Source, hours.Source)],
    };

    private static int Year(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).Year;
}
