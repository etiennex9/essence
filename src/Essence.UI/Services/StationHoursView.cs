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

/// <summary>
/// A station's hours from the API (<see cref="StationHours"/>), against the clock: open, closing soon or closed now, its
/// week, and where the hours come from, in the UI's language. Always in the station's time zone
/// (<see cref="QuebecTime.ZoneFor"/>): a phone set to another zone still sees Laval's 9:30 p.m. Pure: the moment is given.
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
    /// "Costco", "Checked by Essence, November 11", "OpenStreetMap, edited 2025": a brand's name, the day Essence checked
    /// its rule (its month and year when it's not this year), or the year OpenStreetMap's hours were edited.
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
