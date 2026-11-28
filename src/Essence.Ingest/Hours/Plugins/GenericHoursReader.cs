using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Essence.Data;
using Essence.Hours;

namespace Essence.Ingest.Hours.Plugins;

public enum HoursReadOutcome
{
    /// <summary>The hours were read for sure: <see cref="HoursRead.Rule"/>.</summary>
    Rule,

    /// <summary>There are none: an empty list, an empty text, flags that say nothing.</summary>
    None,

    /// <summary>There are some, but they can't be read for sure: <see cref="HoursRead.Error"/> says why.</summary>
    Refused,
}

public sealed record HoursRead(HoursReadOutcome Outcome, string? Rule = null, string? Error = null)
{
    public static readonly HoursRead None = new(HoursReadOutcome.None);

    public static HoursRead Of(string rule) => new(HoursReadOutcome.Rule, rule);

    public static HoursRead Refused(string? error) => new(HoursReadOutcome.Refused, Error: error ?? "they can't be read");
}

/// <summary>
/// A place's hours as a source gives them, whatever their shape, into a canonical opening_hours rule:
/// <list type="bullet">
/// <item>a text: an opening_hours rule ("Mo-Fr 06:00-22:00"), or hours as people write them (<see cref="HoursText"/>);</item>
/// <item>a list of texts: schema.org's <c>openingHours</c>, or lines;</item>
/// <item>a list of days, each with its day ("Monday", "lundi", schema.org's URL, a list of them) and its open and close
/// times (<c>openTime</c>/<c>closeTime</c>, <c>opens</c>/<c>closes</c>, <c>open</c>/<c>close</c>, <c>start</c>/<c>end</c>…),
/// or its times as text, or flags (closed, open 24 hours): schema.org's <c>openingHoursSpecification</c> too, whose
/// entries for a season (<c>validFrom</c>) are skipped;</item>
/// <item>lines of days and times as text ("Mon-Fri." and "6:00am - 9:30pm"), with a code ("open", "closed") or not;</item>
/// <item>an object keyed by day ("monday": {"open": …, "close": …}, "Lun-Ven": "6h-22h"), with an "always open" flag or not;</item>
/// <item>Google's <c>periods</c> (<c>{"open": {"day": 1, "time": "0600"}, "close": …}</c>), whose days left out are closed.</item>
/// </list>
/// Several ranges on a day are split shifts; ranges that overlap, a day left out (but in <c>periods</c>), a range that means
/// the source doesn't say, and a week closed every day refuse the hours.
/// </summary>
public static partial class GenericHoursReader
{
    private static readonly string[] DayKeys = ["dayofweek", "day", "days", "weekday", "weekdays", "jour", "jours", "dayname", "title", "label", "name"];
    private static readonly string[] OpenKeys = ["opentime", "open", "opens", "opening", "openingtime", "openat", "opensat", "start", "starttime", "from", "ouverture", "heureouverture", "debut"];
    private static readonly string[] CloseKeys = ["closetime", "close", "closes", "closing", "closingtime", "closeat", "closesat", "end", "endtime", "to", "fermeture", "heurefermeture", "fin"];
    private static readonly string[] TextKeys = ["time", "times", "hours", "value", "text", "horaire", "heures", "interval", "intervals", "range", "ranges", "openinghours"];
    private static readonly string[] ClosedFlags = ["closed", "isclosed", "ferme", "closedallday"];

    /// <summary>Flags that say the day is open, so closed when false; <c>open</c> too when it's true or false, not a time.</summary>
    private static readonly string[] OpenFlags = ["isopen", "opened", "isopened", "ouvert", "estouvert", "isopenday", "openday"];

    /// <summary>A flag named so, that isn't one of those known, may change the day's hours: it refuses them.</summary>
    private static readonly string[] FlagWords = ["open", "close", "ferm", "ouvert", "holiday", "ferie", "24"];

    /// <summary>Flags of the moment ("isOpenNow", "isToday"), which say nothing of the week's hours.</summary>
    private static readonly string[] MomentWords = ["now", "current", "today", "maintenant", "actuel", "aujourdhui"];

    private static readonly string[] ValidityKeys = ["validfrom", "validthrough", "validfromdate", "validthroughdate"];
    private static readonly string[] Ignored = ["@type", "type", "timezone", "tz", "@context"];

    /// <summary>A key that holds a week's hours inside another ({"hours": {"regular": …}}): one alone is looked into.</summary>
    private static readonly string[] WrapperWords = ["hour", "heure", "horaire", "schedule", "opening", "regular", "normal", "standard", "usual", "week", "semaine", "default"];

    /// <summary>
    /// Parts of a field's name that say it's another department's (a car wash's, a store's, an ATM's), whose hours, 24-hour
    /// flags and status aren't the station's: folded as <see cref="Normalize"/> does.
    /// </summary>
    private static readonly string[] DepartmentParts =
    [
        "carwash", "wash", "lavage", "laveauto", "atm", "guichet", "depanneur", "convenience", "cstore", "store", "shop", "boutique",
        "magasin", "warehouse", "entrepot", "restaurant", "resto", "cafe", "coffee", "kitchen", "food", "deli", "bakery", "boulangerie",
        "propane", "charging", "charger", "recharge", "borne", "pharmac", "office", "bureau", "lottery", "loterie", "garage", "mechanic",
        "mecanique", "repair", "reparation", "tires", "pneu", "lube", "oilchange",
    ];

    /// <summary>A field named for another department than the forecourt (<see cref="DepartmentParts"/>): <c>carWashHours</c>, <c>atm</c>, <c>storeHours</c>.</summary>
    public static bool IsDepartment(string key) => Normalize(key) is var name && DepartmentParts.Any(p => name.Contains(p, StringComparison.Ordinal));

    /// <summary>A flag that says the place, or the day, is open 24 hours: <c>alwaysOpen</c>, <c>siteTwentyFourSeven</c>, <c>open24h</c>…</summary>
    public static bool IsAllDayFlag(string key) =>
        Normalize(key) is var name
        && Regex.IsMatch(
            name,
            "^(is|has|site|store|station|service|forecourt)?(open)?(24h|24hr|24hrs|24hours|24heures|247|twentyfourseven|twentyfourhours|alwaysopen|allday|openallday|ouvert24h|ouvert24heures)$")
        || name is "open24" or "isopen24" or "isopen24hours";

    /// <summary>The keys of a list that holds the hours, inside an object (<c>{"periods": […]}</c>).</summary>
    private static readonly string[] WrapperKeys = ["periods", "openinghoursspecification", "weekdaytext", "regularhours", "regular"];

    /// <param name="unknown">Ranges that mean the source doesn't say (<c>hours.unknown</c>): one of them refuses the hours.</param>
    /// <remarks>
    /// Times on the 12-hour clock anywhere in them, and others in digits from 1 to 12 that could be either, refuse them,
    /// whichever entries they're in (<see cref="HoursText.MixesClocks(IEnumerable{string})"/>).
    /// </remarks>
    public static HoursRead Read(JsonNode? node, IReadOnlyCollection<TimeRange> unknown) =>
        node is not null && HoursText.MixesClocks(Strings(node))
            ? HoursRead.Refused("its times are on the 12-hour clock, and others could be either")
            : Read(node, unknown, depth: 0);

    private static IEnumerable<string> Strings(JsonNode node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
        JsonArray array => array.OfType<JsonNode>().SelectMany(Strings),
        JsonObject obj => obj.Select(m => m.Value).OfType<JsonNode>().SelectMany(Strings),
        _ => [],
    };

    [GeneratedRegex("monday|tuesday|wednesday|thursday|friday|saturday|sunday|lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche")]
    private static partial Regex DayNamePattern();

    private static HoursRead Read(JsonNode? node, IReadOnlyCollection<TimeRange> unknown, int depth) => node switch
    {
        null => HoursRead.None,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => ReadText(value.GetValue<string>(), unknown),
        JsonValue value when value.GetValueKind() == JsonValueKind.Null => HoursRead.None,
        JsonValue => HoursRead.Refused("a number or a flag isn't hours"),
        JsonArray { Count: 0 } => HoursRead.None,
        JsonArray array when array.All(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String) =>
            ReadLines([.. array.Select(i => i!.GetValue<string>())], unknown),
        JsonArray array when array.All(i => i is JsonObject) => ReadEntries([.. array.Cast<JsonObject>()], unknown),
        JsonArray => HoursRead.Refused("a list of hours of several kinds isn't understood"),
        JsonObject obj => ReadObject(obj, unknown, depth),
        _ => HoursRead.Refused(null),
    };

    private static HoursRead ReadText(string text, IReadOnlyCollection<TimeRange> unknown)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return HoursRead.None;
        }

        if (OpeningHours.TryParse(text, lenient: true, out var hours, out _))
        {
            return Checked(hours, unknown);
        }

        return HoursText.Rule(text, unknown, out var error) is { } rule ? HoursRead.Of(rule) : HoursRead.Refused(error);
    }

    /// <summary>schema.org's <c>openingHours</c> ("Mo-Fr 06:00-22:00", "Sa 07:00-20:00"), or lines of text.</summary>
    private static HoursRead ReadLines(IReadOnlyList<string> lines, IReadOnlyCollection<TimeRange> unknown)
    {
        var kept = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (kept.Count == 0)
        {
            return HoursRead.None;
        }

        if (OpeningHours.TryParse(string.Join("; ", kept), lenient: true, out var hours, out _))
        {
            return Checked(hours, unknown);
        }

        return HoursText.Rule(string.Join('\n', kept), unknown, out var error) is { } rule ? HoursRead.Of(rule) : HoursRead.Refused(error);
    }

    /// <summary>An opening_hours rule as read: refused when a day's range means the source doesn't say, or when it's never open.</summary>
    private static HoursRead Checked(OpeningHours hours, IReadOnlyCollection<TimeRange> unknown)
    {
        var closed = true;
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            foreach (var range in hours.On(day))
            {
                if (unknown.Contains(range))
                {
                    return HoursRead.Refused($"{day}'s hours are {range}, which the source gives when it doesn't say");
                }

                closed = false;
            }
        }

        return closed ? HoursRead.Refused("it says it's closed every day") : HoursRead.Of(hours.ToRule());
    }

    /// <summary>A list of days, or of lines, or Google's periods.</summary>
    private static HoursRead ReadEntries(IReadOnlyList<JsonObject> entries, IReadOnlyCollection<TimeRange> unknown)
    {
        if (entries.Any(e => Field(e, "open") is JsonObject open && Field(open, "day") is not null))
        {
            return ReadPeriods(entries, unknown);
        }

        var week = new Dictionary<DayOfWeek, List<TimeRange>>();

        // Each day's entries: several are its split shifts only when each names the same days and none says closed.
        var named = new Dictionary<DayOfWeek, (string Days, bool Closed)>();
        foreach (var entry in entries)
        {
            if (ValidityKeys.Any(k => Field(entry, k) is not null))
            {
                // A season's or a holiday's hours (schema.org's validFrom, validThrough): not the week's.
                continue;
            }

            if (DayKeys.Select(k => Field(entry, k)).FirstOrDefault(n => n is not null) is not { } daysNode)
            {
                return HoursRead.Refused("an entry doesn't say its day");
            }

            if (Days(daysNode) is not { } days)
            {
                return HoursRead.Refused($"the days {Short(daysNode)} aren't understood");
            }

            if (days.Days.Count == 0)
            {
                // The holidays' entry: their caution covers them.
                continue;
            }

            if (Times(entry, skip: DayKeys, days.Days) is not { } times)
            {
                return HoursRead.Refused($"the hours of {Short(daysNode)} aren't understood");
            }

            if (times.Error is { } error)
            {
                return HoursRead.Refused(error);
            }

            var set = string.Join(',', days.Days.Order());
            var closed = times.Ranges.Count == 0;
            foreach (var day in days.Days)
            {
                if (named.TryGetValue(day, out var before) && (before.Days != set || before.Closed || closed))
                {
                    return HoursRead.Refused($"{day}'s hours are given twice, by entries that don't agree");
                }

                named[day] = (set, closed);
                if (!week.TryGetValue(day, out var ranges))
                {
                    week[day] = ranges = [];
                }

                ranges.AddRange(times.Ranges);
            }
        }

        return week.Count == 0 ? HoursRead.None : Rule(week, unknown);
    }

    /// <summary>An object keyed by day ("monday", "Lun-Ven"), or one that holds the hours (<c>periods</c>…).</summary>
    private static HoursRead ReadObject(JsonObject obj, IReadOnlyCollection<TimeRange> unknown, int depth)
    {
        foreach (var key in WrapperKeys)
        {
            if (Field(obj, key) is { } inner)
            {
                // Days or flags beside the list ({"periods": […], "sunday": "Fermé"}, "sundayClosed": true, "closedOn":
                // "Sunday") would say other hours than it: which can't be told. Its other lists, and the moment's flags, are let be.
                return obj.FirstOrDefault(m => Normalize(m.Key) is var name && name != key && !WrapperKeys.Contains(name) && !IsMoment(name)
                        && !Ignored.Contains(m.Key, StringComparer.OrdinalIgnoreCase)
                        && (HoursText.TryDays(m.Key, out _, out _) || IsFlagName(name) || m.Value is JsonValue v && IsBoolean(v)
                            || DayNamePattern().IsMatch(name)))
                    is { Key: { } beside }
                    ? HoursRead.Refused($"\"{beside}\" is given beside its list of hours")
                    : Read(inner, unknown, depth + 1);
            }
        }

        if (obj.FirstOrDefault(m => IsAllDayFlag(m.Key) && m.Value is JsonValue v && v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) is { Key: { } badFlag })
        {
            return HoursRead.Refused($"its flag \"{badFlag}\" isn't true or false");
        }

        var allDay = obj.Any(m => IsAllDayFlag(m.Key) && m.Value?.GetValueKind() == JsonValueKind.True);
        var week = new Dictionary<DayOfWeek, List<TimeRange>>();
        var others = new List<string>();
        foreach (var (key, value) in obj)
        {
            if (IsAllDayFlag(key) || Ignored.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!HoursText.TryDays(key, out var days, out _))
            {
                others.Add(key);
                continue;
            }

            // The holidays' key ("Jours fériés"): their caution covers them.
            if (days.Count == 0)
            {
                continue;
            }

            if (value is null)
            {
                return HoursRead.Refused($"{key}'s hours are null: closed or not said can't be told");
            }

            if (days.Where(week.ContainsKey).Take(1).ToList() is [var twice])
            {
                return HoursRead.Refused($"{twice}'s hours are given twice");
            }

            var times = value switch
            {
                JsonObject entry => NamesOtherDays(entry, days) ? new ParsedTimes([], $"{key}'s hours name other days") : Times(entry, skip: [], days),
                _ => TimesOf(value, days),
            };
            if (times is null || times.Error is not null)
            {
                return HoursRead.Refused(times?.Error ?? $"{key}'s hours {Short(value)} aren't understood");
            }

            foreach (var day in days)
            {
                if (!week.TryGetValue(day, out var ranges))
                {
                    week[day] = ranges = [];
                }

                ranges.AddRange(times.Ranges);
            }
        }

        if (others.Count > 0)
        {
            // One field that holds the week ({"regular": …}), never another department's ({"carWash": …}).
            return week.Count == 0 && others.Count == 1 && depth < 2 && IsWrapper(others[0])
                ? Read(obj[others[0]], unknown, depth + 1)
                : HoursRead.Refused($"\"{others[0]}\" isn't a day");
        }

        if (week.Count == 0)
        {
            return allDay ? HoursRead.Of(OpeningHours.AlwaysOpen.ToRule()) : HoursRead.None;
        }

        var read = Rule(week, unknown);
        return allDay && read.Rule is { } rule && rule != OpeningHours.AlwaysOpen.ToRule()
            ? HoursRead.Refused($"it says it's always open, but its days say {rule}")
            : read;
    }

    /// <summary>Google's periods: day 0 is Sunday, a day left out is closed, and one period opening Sunday at 0000 that never closes is 24/7.</summary>
    private static HoursRead ReadPeriods(IReadOnlyList<JsonObject> periods, IReadOnlyCollection<TimeRange> unknown)
    {
        if (periods is [var only] && Field(only, "close") is null && Moment(Field(only, "open") as JsonObject, end: false) == (0, 0))
        {
            return HoursRead.Of(OpeningHours.AlwaysOpen.ToRule());
        }

        var week = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => new List<TimeRange>());
        foreach (var period in periods)
        {
            // A period is its open and close, each a day and a time: anything beside them ("isOpen": false, "closed": true
            // in its open) would change it.
            if (period.FirstOrDefault(m => Normalize(m.Key) is not ("open" or "close")) is { Key: { } flag })
            {
                return HoursRead.Refused($"a period's field \"{flag}\" isn't understood");
            }

            if (new[] { Field(period, "open"), Field(period, "close") }.OfType<JsonObject>()
                .SelectMany(m => m).FirstOrDefault(m => Normalize(m.Key) is not ("day" or "time" or "hour" or "minute" or "date" or "truncated")) is { Key: { } inside })
            {
                return HoursRead.Refused($"a period's open or close has a field \"{inside}\" that isn't understood");
            }

            if (HoursText.MixesClocks(Text(Field(Field(period, "open") as JsonObject ?? [], "time")), Text(Field(Field(period, "close") as JsonObject ?? [], "time"))))
            {
                return HoursRead.Refused("a period's open and close aren't on the same clock (am or pm on one only)");
            }

            if (Moment(Field(period, "open") as JsonObject, end: false) is not { } opens
                || Moment(Field(period, "close") as JsonObject, end: false) is not { } closes)
            {
                return HoursRead.Refused("a period's open or close isn't understood");
            }

            var (openDay, openMinute) = opens;
            var (closeDay, closeMinute) = closes;
            var end = (closeDay - openDay + 7) % 7 * TimeRange.MinutesPerDay + closeMinute;
            var range = new TimeRange(openMinute, end);
            if (!range.IsValid)
            {
                return HoursRead.Refused($"a period from {openMinute} to {end} isn't a range");
            }

            week[(DayOfWeek)openDay].Add(range);
        }

        return Rule(week, unknown);

        static (int Day, int Minute)? Moment(JsonObject? moment, bool end)
        {
            if (moment is null || Field(moment, "day") is not JsonValue day || !day.TryGetValue<int>(out var number) || number is < 0 or > 6)
            {
                return null;
            }

            if (Field(moment, "time") is JsonValue time && time.GetValueKind() == JsonValueKind.String)
            {
                return HoursText.TryTime(time.GetValue<string>(), end, out var clock) ? (number, clock) : null;
            }

            if (Field(moment, "hour") is not JsonValue hour || !hour.TryGetValue<int>(out var h) || h is < 0 or > 23)
            {
                return null;
            }

            var minutes = 0;
            if (Field(moment, "minute") is JsonValue minute && (!minute.TryGetValue(out minutes) || minutes is < 0 or > 59))
            {
                return null;
            }

            return (number, h * 60 + minutes);
        }
    }

    private static HoursRead Rule(Dictionary<DayOfWeek, List<TimeRange>> week, IReadOnlyCollection<TimeRange> unknown) =>
        HoursText.WeekRule(week.ToDictionary(d => d.Key, d => (IReadOnlyList<TimeRange>)d.Value), unknown, out var error) is { } rule
            ? HoursRead.Of(rule)
            : HoursRead.Refused(error);

    private sealed record ParsedDays(IReadOnlyList<DayOfWeek> Days, bool Holiday);

    /// <summary>
    /// "Monday", "Mon-Fri.", "https://schema.org/Monday", ["Monday", "Tuesday"], with holidays or not ("Dimanche et fériés",
    /// schema.org's <c>PublicHolidays</c>), which are left out of the days; null when not understood (a number among them).
    /// </summary>
    private static ParsedDays? Days(JsonNode node)
    {
        switch (node)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return One(value.GetValue<string>());
            case JsonArray array:
                var list = new List<DayOfWeek>();
                var holiday = false;
                foreach (var item in array)
                {
                    if (item is not JsonValue text || text.GetValueKind() != JsonValueKind.String || One(text.GetValue<string>()) is not { } some)
                    {
                        return null;
                    }

                    list.AddRange(some.Days);
                    holiday |= some.Holiday;
                }

                return new ParsedDays(list.Distinct().ToList(), holiday);
            default:
                return null;
        }

        static ParsedDays? One(string name) =>
            HoursText.TryDay(name, out var one) ? new ParsedDays([one], false)
            : Normalize(name).EndsWith("publicholidays", StringComparison.Ordinal) ? new ParsedDays([], true)
            : HoursText.TryDays(name, out var days, out var holiday) ? new ParsedDays(days, holiday)
            : null;
    }

    private sealed record ParsedTimes(IReadOnlyList<TimeRange> Ranges, string? Error = null);

    /// <summary>
    /// A day's times in an entry: its flags (closed, open, open 24 hours), its open and close times, or its times as text. A
    /// day is closed only when a flag says so: open and close times that aren't texts read ("", 600, {"hour": 6}), a flag
    /// that isn't true or false, or one it doesn't know named for the hours ("isHoliday"), refuse it.
    /// </summary>
    private static ParsedTimes? Times(JsonObject entry, IReadOnlyList<string> skip, IReadOnlyCollection<DayOfWeek>? days)
    {
        // What its flags say: closed (closed, isOpen false), open (closed false, isOpen true), open 24 hours.
        var (closed, saysOpen, allDay) = (false, false, false);
        foreach (var (key, value) in entry)
        {
            if (value is null)
            {
                continue;
            }

            var name = Normalize(key);
            var kind = value.GetValueKind();
            var boolean = kind is JsonValueKind.True or JsonValueKind.False;
            var (isClosed, isOpen, isAllDay) = (ClosedFlags.Contains(name), OpenFlags.Contains(name) || (name == "open" && boolean), IsAllDayFlag(key));
            if (isClosed || isOpen || isAllDay)
            {
                if (!boolean)
                {
                    return new ParsedTimes([], $"its flag \"{key}\" isn't true or false");
                }

                var yes = kind == JsonValueKind.True;
                closed |= isClosed ? yes : isOpen && !yes;
                saysOpen |= isClosed ? !yes : isOpen && yes;
                allDay |= isAllDay && yes;
                continue;
            }

            if (skip.Contains(name) || KnownFields.Contains(name) || IsMoment(name))
            {
                continue;
            }

            if (IsStatusName(name))
            {
                // A day's status must say it's open: "closed", "fermé", {"code": "closed"} or a word it doesn't know refuse it.
                if (Text(value) is not { } status || PluginStatusWords.Read(status) != PluginStatus.Open)
                {
                    return new ParsedTimes([], $"its status {Short(value)} doesn't say it's open");
                }

                continue;
            }

            if (HoursText.TryDays(key, out _, out _))
            {
                // {"day": "Mon-Sun", …, "sunday": "closed"}: another day's hours inside these.
                return new ParsedTimes([], $"its field \"{key}\" names a day");
            }

            if (boolean || IsFlagName(name) || Text(value) is { } said && PluginStatusWords.Read(said) != PluginStatus.Unknown)
            {
                // "dayOff": true, "available": false, "isHoliday": 1, "note": "Fermé": of the day's hours, but not understood.
                return new ParsedTimes([], $"its field \"{key}\" isn't understood");
            }
        }

        if (closed && (saysOpen || allDay))
        {
            return new ParsedTimes([], "it says it's closed and open");
        }

        if (closed || allDay)
        {
            return new ParsedTimes(closed ? [] : [TimeRange.AllDay]);
        }

        var open = OpenKeys.Select(k => Field(entry, k)).FirstOrDefault(n => n is not null && !IsBoolean(n));
        var close = CloseKeys.Select(k => Field(entry, k)).FirstOrDefault(n => n is not null && !IsBoolean(n));
        if (open is not null || close is not null)
        {
            var (from, to) = (Text(open), Text(close));
            if (from is null || to is null)
            {
                return new ParsedTimes([], $"the hours {Short(open)} to {Short(close)} aren't understood");
            }

            // schema.org closes a day with "00:00" to "00:00", where other sources mean the whole day: which is meant can't be told.
            if (Field(entry, "opens") is not null && HoursText.TryTime(from, false, out var o) && o == 0
                && HoursText.TryTime(to, false, out var c) && c == 0)
            {
                return new ParsedTimes([], "schema.org's \"00:00\" to \"00:00\" means closed there, the whole day elsewhere: which is meant can't be told");
            }

            return HoursText.TryRange(from, to, out var range) ? new ParsedTimes([range]) : new ParsedTimes([], $"the hours \"{from}\" to \"{to}\" aren't understood");
        }

        var text = TextKeys.Select(k => Field(entry, k)).FirstOrDefault(n => n is not null)
            ?? entry.Where(m => !skip.Contains(Normalize(m.Key)) && !IsDepartment(m.Key)).Select(m => m.Value).SingleOrDefaultSafe();
        if (text is null)
        {
            return null;
        }

        // Costco's lines: a code that says "open" or "closed" with the time.
        if (Text(Field(entry, "code")) is { } code && Text(text) is { } line && RuleNormalizer.TryTimes(code, line, out var coded))
        {
            return new ParsedTimes(coded);
        }

        return TimesOf(text, days);
    }

    /// <summary>Times as text ("6h-22h", "Fermé"), or a list of them or of objects with open and close times.</summary>
    private static ParsedTimes? TimesOf(JsonNode node, IReadOnlyCollection<DayOfWeek>? days)
    {
        switch (node)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return HoursText.TryTimes(value.GetValue<string>(), out var ranges) ? new ParsedTimes(ranges) : null;
            case JsonArray array:
                var all = new List<TimeRange>();
                var closed = false;
                foreach (var item in array)
                {
                    var times = item switch
                    {
                        JsonObject entry when NamesOtherDays(entry, days) => new ParsedTimes([], "a day's hours name other days"),
                        JsonObject entry => Times(entry, skip: [], days),
                        JsonNode other => TimesOf(other, days),
                        null => null,
                    };
                    if (times is null || times.Error is not null)
                    {
                        return times;
                    }

                    closed |= times.Ranges.Count == 0;
                    all.AddRange(times.Ranges);
                }

                // ["6h-22h", "Fermé"]: closed and open at once.
                return closed && all.Count > 0 ? new ParsedTimes([], "its hours say closed and open") : new ParsedTimes(all);
            default:
                return null;
        }
    }

    private static bool IsBoolean(JsonNode node) => node.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    /// <summary>The fields of a day's entry that it reads, or that say nothing of its hours.</summary>
    private static readonly HashSet<string> KnownFields =
        [.. DayKeys, .. OpenKeys, .. CloseKeys, .. TextKeys, .. ValidityKeys, .. Ignored.Select(Normalize), "code"];

    /// <summary>Named for a day's hours (open, closed, holiday, 24…), and not a flag of the moment ("isOpenNow").</summary>
    private static bool IsFlagName(string name) =>
        FlagWords.Any(w => name.Contains(w, StringComparison.Ordinal)) && !MomentWords.Any(w => name.Contains(w, StringComparison.Ordinal));

    /// <summary>
    /// An entry inside the hours of <paramref name="days"/> that names other days ({"day": "Sunday", "open": …} among
    /// Monday to Sunday's), or a day it can't read ({"day": 0}, "Sundays only"): whose hours they are can't be told.
    /// Naming the same days again is let be. A <c>name</c>, <c>label</c> or <c>title</c> counts only when it's days.
    /// </summary>
    private static bool NamesOtherDays(JsonObject entry, IReadOnlyCollection<DayOfWeek>? days) =>
        DayKeys.Any(k => Field(entry, k) is { } node && (Days(node) is { } named
            ? days is null || !named.Days.ToHashSet().SetEquals(days) || named.Holiday
            : !GenericDayKeys.Contains(k)));

    /// <summary>Day keys that are as likely a range's own name ("Matin"): read as days only when they're days.</summary>
    private static readonly string[] GenericDayKeys = ["title", "label", "name"];

    private static bool IsMoment(string name) => MomentWords.Any(w => name.Contains(w, StringComparison.Ordinal));

    /// <summary>A day's status: <c>status</c>, <c>statut</c>, <c>state</c>, <c>état</c>, <c>dayStatus</c>.</summary>
    private static bool IsStatusName(string name) =>
        name is "status" or "statut" or "state" or "etat" || name.EndsWith("status", StringComparison.Ordinal) || name.EndsWith("statut", StringComparison.Ordinal);

    private static bool IsWrapper(string key) =>
        !IsDepartment(key) && Normalize(key) is var name && WrapperWords.Any(w => name.Contains(w, StringComparison.Ordinal));

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A field by its name folded (<see cref="Normalize"/>): <c>openTime</c>, <c>open_time</c> and <c>OpenTime</c> are one.</summary>
    private static JsonNode? Field(JsonObject obj, string normalized)
    {
        foreach (var (key, value) in obj)
        {
            if (Normalize(key) == normalized)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>"open_time" and "Open Time" are <c>opentime</c>: folded, letters and digits only.</summary>
    public static string Normalize(string key)
    {
        var folded = StationKey.Fold(key);
        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '@')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 60 ? text[..57] + "…" : text;
    }

    private static JsonNode? SingleOrDefaultSafe(this IEnumerable<JsonNode?> nodes)
    {
        var list = nodes.Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }
}
