using System.Globalization;
using System.Text.RegularExpressions;
using Essence.Data;
using Essence.Hours;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>
/// Hours as people write them, in French or English: "Lun-Ven : 6 h à 22 h; Sam. et dim. 7h-21h", "Lundi au vendredi de
/// 6h00 à 22h00", "Mon-Fri 6am-10pm, Sat 7:00-21:00", "Tous les jours 6h-23h", "Ouvert 24 h/24, 7j/7", "24 heures",
/// "Fermé", with "midi" and "minuit". Only what can be told for sure is read: a word it doesn't know ("sauf", "été", a
/// note), a day given twice, a day left out or hours without days when other days have some refuse the text, with the
/// reason. A line of holidays alone is skipped, the holidays' caution covering those days; the days named with them
/// ("Dimanche et jours fériés") get the line's hours, so a day given on another line too refuses the text.
/// </summary>
public static partial class HoursText
{
    private const string AllDayMark = "#q1#";
    private const string EveryDayMark = "#q2#";
    private const string WeekendMark = "#q3#";
    private const string WeekdaysMark = "#q4#";
    private const string HolidayMark = "#q5#";
    private const string ClosedMark = "#q6#";

    private static readonly IReadOnlyList<DayOfWeek> Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.Ordinal)
    {
        ["mo"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["lu"] = DayOfWeek.Monday, ["lun"] = DayOfWeek.Monday, ["lundi"] = DayOfWeek.Monday,
        ["tu"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["ma"] = DayOfWeek.Tuesday, ["mar"] = DayOfWeek.Tuesday, ["mardi"] = DayOfWeek.Tuesday,
        ["we"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["me"] = DayOfWeek.Wednesday, ["mer"] = DayOfWeek.Wednesday, ["mercredi"] = DayOfWeek.Wednesday,
        ["th"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["thursday"] = DayOfWeek.Thursday, ["je"] = DayOfWeek.Thursday, ["jeu"] = DayOfWeek.Thursday, ["jeudi"] = DayOfWeek.Thursday,
        ["fr"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["ve"] = DayOfWeek.Friday, ["ven"] = DayOfWeek.Friday, ["vendredi"] = DayOfWeek.Friday,
        ["sa"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["sam"] = DayOfWeek.Saturday, ["samedi"] = DayOfWeek.Saturday,
        ["su"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
        ["di"] = DayOfWeek.Sunday, ["dim"] = DayOfWeek.Sunday, ["dimanche"] = DayOfWeek.Sunday,
    };

    /// <summary>Words that say nothing about when: "de 6 h à 22 h", "Ouvert", "Heures d'ouverture :".</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "de", "du", "des", "d", "from", "le", "la", "les", "l", "the", "on", "at", "open", "opened", "opening", "ouvert", "ouverte",
        "ouverts", "ouvertes", "hours", "heures", "heure", "horaire", "horaires", "hrs", "ouverture", "is", "are", "d'", "l'",
    };

    private static readonly HashSet<string> RangeWords = new(StringComparer.Ordinal) { "a", "au", "to" };

    private static readonly HashSet<string> AndWords = new(StringComparer.Ordinal) { "et", "and" };

    /// <summary>A day's name or abbreviation in French or English, in any case, a "." after it or not, or schema.org's <c>https://schema.org/Monday</c>.</summary>
    public static bool TryDay(string? name, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var text = StationKey.Fold(name).Trim().TrimEnd('.');
        if (text.LastIndexOfAny(['/', ':', '#']) is var cut and >= 0)
        {
            text = text[(cut + 1)..];
        }

        return DayNames.TryGetValue(text, out day) || (text.EndsWith('s') && DayNames.TryGetValue(text[..^1], out day));
    }

    /// <summary>
    /// A time of day: "06:00", "6:00", "06:00:00", "6h", "6 h 30", "6h30", "0600", "6am", "6:30 p.m.", "midi", "noon",
    /// "minuit", "midnight". As an <paramref name="end"/>, midnight ("00:00", "12am", "minuit", "24:00", "24h") is 1440.
    /// </summary>
    public static bool TryTime(string? text, bool end, out int minute)
    {
        minute = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var folded = Spaces().Replace(StationKey.Fold(text).Trim(), " ");
        if (folded is "midi" or "noon" or "12 noon")
        {
            minute = 12 * 60;
            return true;
        }

        if (folded is "minuit" or "midnight")
        {
            minute = end ? TimeRange.MinutesPerDay : 0;
            return true;
        }

        if (ClockPattern().Match(folded) is not { Success: true } match)
        {
            return false;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (minutes > 59)
        {
            return false;
        }

        if (match.Groups["half"].Success)
        {
            if (hour is < 1 or > 12)
            {
                return false;
            }

            hour = hour % 12 + (match.Groups["half"].Value[0] == 'p' ? 12 : 0);
        }
        else if (hour > 24 || hour == 24 && (minutes > 0 || !end))
        {
            return false;
        }

        minute = hour * 60 + minutes;
        if (end && minute == 0)
        {
            minute = TimeRange.MinutesPerDay;
        }

        return true;
    }

    /// <summary>
    /// A range from <paramref name="open"/> to <paramref name="close"/> (<see cref="TryTime"/>): an end at or before the start
    /// is past midnight, "00:00" to "00:00" is the whole day, and the same start and end otherwise isn't a range. One end
    /// in am or pm and the other a bare hour that could be either ("1:00 - 6:00 pm") isn't one: which is meant can't be told.
    /// </summary>
    public static bool TryRange(string? open, string? close, out TimeRange range)
    {
        range = default;
        if (!TryTime(open, end: false, out var start) || !TryTime(close, end: true, out var stop) || MixesClocks(open, close))
        {
            return false;
        }

        range = new TimeRange(start, stop <= start ? stop + TimeRange.MinutesPerDay : stop);
        return range.IsValid;
    }

    /// <summary>
    /// One end on the 12-hour clock (am, pm, "noon") and the other an hour from 1 to 12 that could be either ("1:00 - 6:00
    /// pm", "noon - 6:00"): which is meant can't be told. French hours ("6 h", "18h30") are the 24-hour clock's.
    /// </summary>
    public static bool MixesClocks(string? open, string? close) =>
        (ClockOf(open), ClockOf(close)) is (Clock.Half, Clock.Bare) or (Clock.Bare, Clock.Half);

    private enum Clock
    {
        /// <summary>A word ("midi", "minuit"), a French hour ("6 h"), or an hour that can only be one (0, 13 to 24).</summary>
        Other,

        /// <summary>With am or pm, or "noon".</summary>
        Half,

        /// <summary>An hour from 1 to 12 in digits, without am or pm.</summary>
        Bare,
    }

    /// <summary>
    /// The texts hold times on the 12-hour clock (am, pm, "noon", "midnight") and others, in digits from 1 to 12, that could
    /// be either: "Mon-Sun 6am-10pm, 1:00-6:00". Which is meant can't be told, whichever ends they're the ranges of.
    /// </summary>
    public static bool MixesClocks(IEnumerable<string> texts)
    {
        var (half, bare) = (false, false);
        foreach (var text in texts)
        {
            var folded = Spaces().Replace(StationKey.Fold(text).Trim(), " ");
            IEnumerable<string> times = FourDigitsPattern().IsMatch(folded) ? [folded] : AnyTimePattern().Matches(folded).Select(m => m.Value);
            foreach (var time in times)
            {
                var clock = ClockOf(time);
                (half, bare) = (half || clock == Clock.Half, bare || clock == Clock.Bare);
            }
        }

        return half && bare;
    }

    private static Clock ClockOf(string? text)
    {
        var folded = Spaces().Replace(StationKey.Fold(text ?? "").Trim(), " ");
        if (folded is "noon" or "12 noon" or "midnight" or "12 midnight")
        {
            return Clock.Half;
        }

        if (ClockPattern().Match(folded) is not { Success: true } match)
        {
            return Clock.Other;
        }

        return match.Groups["half"].Success ? Clock.Half
            : !folded.Contains(':') && folded.Contains('h') ? Clock.Other
            : int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) is >= 1 and <= 12 ? Clock.Bare
            : Clock.Other;
    }

    /// <summary>"Lun-Ven", "Mon.-Fri.", "Sam. et dim.", "lundi au vendredi", "Tous les jours", "Fin de semaine", "Monday".</summary>
    /// <param name="holiday">The text names holidays ("Jours fériés"), not days.</param>
    public static bool TryDays(string? text, out IReadOnlyList<DayOfWeek> days, out bool holiday)
    {
        (days, holiday) = ([], false);
        if (Tokenize(text ?? "", out _) is not { Count: > 0 } tokens)
        {
            return false;
        }

        var i = 0;
        if (ParseDays(tokens, ref i) is not { } parsed || i != tokens.Count)
        {
            return false;
        }

        (days, holiday) = (parsed.Days, parsed.Holiday);
        return true;
    }

    /// <summary>"6 h à 22 h", "06:00-22:00, 23:00-02:00", "6am - 10pm", "24 h", "Fermé" (no range: closed).</summary>
    public static bool TryTimes(string? text, out IReadOnlyList<TimeRange> ranges)
    {
        ranges = [];
        if (Tokenize(text ?? "", out _) is not { Count: > 0 } tokens || MixesClocks([text!]))
        {
            return false;
        }

        var i = 0;
        if (ParseTimes(tokens, ref i) is not { } parsed || i != tokens.Count)
        {
            return false;
        }

        ranges = parsed;
        return true;
    }

    /// <summary>The week's canonical rule from the text, or null with the reason.</summary>
    /// <param name="unknown">Ranges that mean the source doesn't say (<c>hours.unknown</c>): one of them refuses the text.</param>
    public static string? Rule(string text, IReadOnlyCollection<TimeRange> unknown, out string? error)
    {
        if (Tokenize(text, out error) is not { } tokens)
        {
            return null;
        }

        if (tokens.Count == 0)
        {
            error = "it says nothing";
            return null;
        }

        if (MixesClocks([text]))
        {
            error = "it has times on the 12-hour clock and others that could be either";
            return null;
        }

        var week = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>();
        var hasDays = tokens.Any(t => t.Kind is Kind.Day or Kind.Group);
        var i = 0;
        while (i < tokens.Count)
        {
            if (tokens[i].Kind is Kind.Clause or Kind.Comma or Kind.And)
            {
                i++;
                continue;
            }

            var days = ParseDays(tokens, ref i);
            IReadOnlyList<TimeRange>? times;
            if (days is null)
            {
                times = ParseTimes(tokens, ref i);
                if (times is null)
                {
                    error = $"\"{tokens[i].Text}\" isn't understood there";
                    return null;
                }

                // "24 h/24, 7j/7", "6h-22h tous les jours": the days after the hours.
                if (i + 1 < tokens.Count && tokens[i].Kind == Kind.Comma && tokens[i + 1].Kind is Kind.Day or Kind.Group or Kind.Holiday)
                {
                    i++;
                }

                days = ParseDays(tokens, ref i);
                if (days is null && hasDays)
                {
                    error = "some hours aren't given days";
                    return null;
                }

                days ??= new ParsedDays(Week, false);
            }
            else
            {
                times = ParseTimes(tokens, ref i);
                if (times is null)
                {
                    error = i < tokens.Count ? $"\"{tokens[i].Text}\" isn't understood there" : "some days aren't given hours";
                    return null;
                }
            }

            // The holidays' own line is skipped (their caution covers them); "Dimanche et jours fériés 8h-20h" gives Sunday's.
            if (days.Days.Count == 0)
            {
                continue;
            }

            foreach (var day in days.Days)
            {
                if (!week.TryAdd(day, times))
                {
                    error = $"{day}'s hours are given twice";
                    return null;
                }
            }
        }

        return WeekRule(week, unknown, out error);
    }

    /// <summary>
    /// The rule of a week read from any shape: every day given once, no ranges of a day overlapping, none that means the
    /// source doesn't say, and not closed every day (a place that's always closed isn't a station's hours).
    /// </summary>
    public static string? WeekRule(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TimeRange>> week, IReadOnlyCollection<TimeRange> unknown, out string? error)
    {
        foreach (var (day, ranges) in week)
        {
            foreach (var range in ranges.Where(unknown.Contains))
            {
                error = $"{day}'s hours are {range}, which the source gives when it doesn't say";
                return null;
            }

            var sorted = ranges.OrderBy(r => r.StartMinute).ToList();
            for (var k = 1; k < sorted.Count; k++)
            {
                if (sorted[k].StartMinute < sorted[k - 1].EndMinute)
                {
                    error = $"{day}'s hours {sorted[k - 1]} and {sorted[k]} overlap";
                    return null;
                }
            }
        }

        if (week.Count == 7 && week.Values.All(r => r.Count == 0))
        {
            error = "it says it's closed every day";
            return null;
        }

        return RuleNormalizer.Week(week, out error);
    }

    private sealed record ParsedDays(IReadOnlyList<DayOfWeek> Days, bool Holiday);

    /// <summary>Days at <paramref name="i"/>: "lun", "lun-ven", "lun, mer et ven", "#weekend#"; null when there are none.</summary>
    private static ParsedDays? ParseDays(IReadOnlyList<Token> tokens, ref int i)
    {
        var days = new List<DayOfWeek>();
        var holiday = false;
        var start = i;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Day)
            {
                if (i + 2 < tokens.Count && tokens[i + 1].Kind == Kind.Range && tokens[i + 2].Kind == Kind.Day)
                {
                    days.AddRange(RuleNormalizer.Range(token.Day, tokens[i + 2].Day));
                    i += 3;
                }
                else
                {
                    days.Add(token.Day);
                    i++;
                }
            }
            else if (token.Kind == Kind.Group)
            {
                days.AddRange(token.Days);
                i++;
            }
            else if (token.Kind == Kind.Holiday)
            {
                holiday = true;
                i++;
            }
            else
            {
                break;
            }

            // "lun, mer et ven", "sam/dim": more days follow.
            if (i + 1 < tokens.Count && tokens[i].Kind is Kind.Comma or Kind.And or Kind.Slash
                && tokens[i + 1].Kind is Kind.Day or Kind.Group or Kind.Holiday)
            {
                i++;
            }
            else
            {
                break;
            }
        }

        return i == start ? null : new ParsedDays(days.Distinct().ToList(), holiday);
    }

    /// <summary>Hours at <paramref name="i"/>: "6h-22h", "6h-12h et 13h-22h", "#closed#" (none), "#allday#", "24 h"; null when there are none.</summary>
    private static List<TimeRange>? ParseTimes(IReadOnlyList<Token> tokens, ref int i)
    {
        if (i >= tokens.Count)
        {
            return null;
        }

        switch (tokens[i].Kind)
        {
            case Kind.Closed:
                i++;
                return [];
            case Kind.AllDay:
                i++;
                return [TimeRange.AllDay];
            case Kind.Time when !(i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.Range) && IsTwentyFour(tokens[i].Text):
                i++;
                return [TimeRange.AllDay];
        }

        var ranges = new List<TimeRange>();
        while (i + 2 < tokens.Count && tokens[i].Kind == Kind.Time && tokens[i + 1].Kind == Kind.Range && tokens[i + 2].Kind == Kind.Time)
        {
            if (!TryRange(tokens[i].Text, tokens[i + 2].Text, out var range))
            {
                return null;
            }

            ranges.Add(range);
            i += 3;
            if (i + 1 < tokens.Count && tokens[i].Kind is Kind.Comma or Kind.And or Kind.Slash && tokens[i + 1].Kind == Kind.Time)
            {
                i++;
            }
            else
            {
                break;
            }
        }

        return ranges.Count > 0 ? ranges : null;
    }

    private static bool IsTwentyFour(string text) => TwentyFourPattern().IsMatch(text);

    private enum Kind
    {
        Day,
        Group,
        Holiday,
        Range,
        And,
        Slash,
        Comma,
        Clause,
        Time,
        AllDay,
        Closed,
    }

    private sealed record Token(Kind Kind, string Text, DayOfWeek Day = default, IReadOnlyList<DayOfWeek>? Group = null)
    {
        public IReadOnlyList<DayOfWeek> Days => Group ?? [Day];
    }

    /// <summary>The text's tokens, words that say nothing dropped; null with the reason when a word isn't understood.</summary>
    private static List<Token>? Tokenize(string text, out string? error)
    {
        error = null;
        if (text.Contains('<'))
        {
            text = PluginDocument.TextOf(text);
        }

        var folded = StationKey.Fold(text)
            .Replace('–', '-').Replace('—', '-').Replace('‑', '-').Replace('−', '-')
            .Replace('’', '\'').Replace(' ', ' ').Replace(' ', ' ');
        folded = AllDayAroundPattern().Replace(folded, $" {AllDayMark} ");
        folded = AllWeekAllDayPattern().Replace(folded, $" {AllDayMark} {EveryDayMark} ");
        folded = EveryDayPattern().Replace(folded, $" {EveryDayMark} ");
        folded = AllDayWordsPattern().Replace(folded, $" {AllDayMark} ");
        folded = WeekendPattern().Replace(folded, $" {WeekendMark} ");
        folded = WeekdaysPattern().Replace(folded, $" {WeekdaysMark} ");
        folded = HolidayPattern().Replace(folded, $" {HolidayMark} ");
        folded = ClosedPattern().Replace(folded, $" {ClosedMark} ");
        folded = UntilPattern().Replace(folded, " - ");

        var tokens = new List<Token>();
        for (var p = 0; p < folded.Length;)
        {
            var c = folded[p];
            if (char.IsWhiteSpace(c) && c != '\n')
            {
                p++;
                continue;
            }

            switch (c)
            {
                case '\n' or ';' or '|':
                    tokens.Add(new Token(Kind.Clause, c.ToString()));
                    p++;
                    continue;
                case ',':
                    tokens.Add(new Token(Kind.Comma, ","));
                    p++;
                    continue;
                case '-':
                    tokens.Add(new Token(Kind.Range, "-"));
                    p++;
                    continue;
                case '&' or '+':
                    tokens.Add(new Token(Kind.And, c.ToString()));
                    p++;
                    continue;
                case '/':
                    tokens.Add(new Token(Kind.Slash, "/"));
                    p++;
                    continue;
                case ':' or '(' or ')' or '[' or ']' or '.' or '"' or '*' or '!':
                    p++;
                    continue;
                case '#':
                    var end = folded.IndexOf('#', p + 1);
                    var mark = end < 0 ? "#" : folded[p..(end + 1)];
                    if (mark is not (AllDayMark or EveryDayMark or WeekendMark or WeekdaysMark or HolidayMark or ClosedMark))
                    {
                        error = "\"#\" isn't understood";
                        return null;
                    }

                    tokens.Add(mark switch
                    {
                        AllDayMark => new Token(Kind.AllDay, mark),
                        EveryDayMark => new Token(Kind.Group, mark, Group: Week),
                        WeekendMark => new Token(Kind.Group, mark, Group: [DayOfWeek.Saturday, DayOfWeek.Sunday]),
                        WeekdaysMark => new Token(Kind.Group, mark, Group: Week.Take(5).ToList()),
                        HolidayMark => new Token(Kind.Holiday, mark),
                        _ => new Token(Kind.Closed, mark),
                    });
                    p = end + 1;
                    continue;
            }

            if (TimeTokenPattern().Match(folded, p) is { Success: true } time && time.Index == p)
            {
                tokens.Add(new Token(Kind.Time, time.Value));
                p += time.Length;
                continue;
            }

            if (WordPattern().Match(folded, p) is { Success: true } word && word.Index == p)
            {
                var value = word.Value;
                p += word.Length;
                var bare = value.TrimEnd('.');
                if (bare.Length > 2 && bare[1] == '\'' && bare[0] is 'd' or 'l')
                {
                    bare = bare[2..];
                }

                if (bare is "midi" or "noon" or "minuit" or "midnight")
                {
                    tokens.Add(new Token(Kind.Time, bare));
                }
                else if (TryDay(bare, out var day))
                {
                    tokens.Add(new Token(Kind.Day, bare, day));
                }
                else if (RangeWords.Contains(bare))
                {
                    tokens.Add(new Token(Kind.Range, bare));
                }
                else if (AndWords.Contains(bare))
                {
                    tokens.Add(new Token(Kind.And, bare));
                }
                else if (!Fillers.Contains(bare))
                {
                    error = $"the word \"{bare}\" isn't understood";
                    return null;
                }

                continue;
            }

            error = $"\"{folded[p..Math.Min(folded.Length, p + 12)].Trim()}\" isn't understood";
            return null;
        }

        return tokens;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>"06:00", "6:00:00", "6h", "6 h 30", "6h30", "0600", "6am", "6:30 p.m.", "24:00".</summary>
    [GeneratedRegex(@"^(?:(?<h>\d{1,2})\s*:\s*(?<m>\d{2})(?::\d{2})?(?:\s*(?<half>[ap])\.?\s*m\.?)?|(?<h>\d{1,2})\s*h\s*(?<m>\d{2})?|(?<h>\d{2})(?<m>\d{2})|(?<h>\d{1,2})\s*(?<half>[ap])\.?\s*m\.?)$")]
    private static partial Regex ClockPattern();

    /// <summary>A time in a text, at the position tried: "6:30 p.m.", "6 h 30", "6h", "06:00", "6am".</summary>
    [GeneratedRegex(@"\G(?:\d{1,2}\s*:\s*\d{2}(?::\d{2})?(?:\s*[ap]\.?\s*m\b\.?)?|\d{1,2}\s*h(?:\s*\d{2})?(?![a-z0-9])|\d{1,2}\s*[ap]\.?\s*m\b\.?)")]
    private static partial Regex TimeTokenPattern();

    [GeneratedRegex(@"\G[a-z][a-z']*\.?")]
    private static partial Regex WordPattern();

    /// <summary>Any time in a text, for <see cref="MixesClocks(IEnumerable{string})"/>: as <see cref="TimeTokenPattern"/>, and "noon", "midnight".</summary>
    [GeneratedRegex(@"\d{1,2}\s*:\s*\d{2}(?::\d{2})?(?:\s*[ap]\.?\s*m\b\.?)?|\d{1,2}\s*h(?:\s*\d{2})?(?![a-z0-9])|\d{1,2}\s*[ap]\.?\s*m\b\.?|\bnoon\b|\bmidnight\b")]
    private static partial Regex AnyTimePattern();

    /// <summary>A time alone in four digits: Google's "0600".</summary>
    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex FourDigitsPattern();

    [GeneratedRegex(@"^24\s*(?:h|:00)$")]
    private static partial Regex TwentyFourPattern();

    /// <summary>"24h/24", "24 heures sur 24", "ouvert 24 h / 24".</summary>
    [GeneratedRegex(@"\b24\s*(?:h|hrs?|heures?|hours?)?\s*(?:/|sur)\s*24(?:\s*h\b)?")]
    private static partial Regex AllDayAroundPattern();

    /// <summary>"24/7".</summary>
    [GeneratedRegex(@"\b24\s*/\s*7\b")]
    private static partial Regex AllWeekAllDayPattern();

    /// <summary>"7j/7", "7 jours sur 7", "7/7", "tous les jours", "every day", "daily", "7 days a week".</summary>
    [GeneratedRegex(@"\b7\s*j(?:ours)?\s*(?:/|sur)\s*7\b|\b7\s*/\s*7\b|\btous\s+les\s+jours\b|\bchaque\s+jour\b|\btlj\b|\bevery\s*day\b|\bdaily\b|\ball\s+week\b|\b7\s+(?:jours|days)(?:\s+(?:a|per)\s+week|\s+par\s+semaine)?\b|\bseven\s+days(?:\s+a\s+week)?\b")]
    private static partial Regex EveryDayPattern();

    /// <summary>"24 heures", "24 hours", "24 hrs", "open 24 hours a day", "around the clock".</summary>
    [GeneratedRegex(@"\b24\s*(?:heures|hours?|hrs?)(?:\s+a\s+day)?\b|\baround\s+the\s+clock\b")]
    private static partial Regex AllDayWordsPattern();

    [GeneratedRegex(@"\bfins?\s+de\s+semaines?\b|\bweek-?ends?\b")]
    private static partial Regex WeekendPattern();

    [GeneratedRegex(@"\ben\s+semaine\b|\bjours?\s+de\s+(?:la\s+)?semaine\b|\bweekdays?\b")]
    private static partial Regex WeekdaysPattern();

    [GeneratedRegex(@"\bjours?\s+feries?\b|\bferies?\b|\b(?:public\s+|statutory\s+|stat\s+)?holidays?\b|\bconges?\b")]
    private static partial Regex HolidayPattern();

    [GeneratedRegex(@"\bfermee?s?\b|\bclosed\b")]
    private static partial Regex ClosedPattern();

    [GeneratedRegex(@"\bjusqu'?\s*a\b|\buntil\b|\btill\b|\bthrough\b|\bthru\b")]
    private static partial Regex UntilPattern();
}
