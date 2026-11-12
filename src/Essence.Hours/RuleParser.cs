using System.Collections.ObjectModel;
using System.Globalization;

namespace Essence.Hours;

/// <summary>
/// Reads opening_hours for <see cref="OpeningHours.TryParse"/>: a small tokenizer and a recursive descent rather than a
/// regex, so that each refusal can say what's wrong and where. A refusal is a <see cref="FormatException"/> whose message
/// is the reason.
/// </summary>
internal sealed class RuleParser
{
    /// <summary>Where the holidays' hours are kept, after the days'.</summary>
    private const int Holidays = 7;

    private static readonly bool[] Everyday = [true, true, true, true, true, true, true];

    private readonly string _text;
    private readonly List<Token> _tokens;
    private readonly bool _lenient;
    private readonly StringComparison _case;
    private int _index;

    // The hours the rules so far gave each day (by DayOfWeek), then the holidays: null until a rule names it.
    private readonly List<TimeRange>?[] _set = new List<TimeRange>?[8];

    // The days named, by a rule without PH, since PH's hours were set: on the holidays that fall then, their hours replace PH's.
    private readonly bool[] _namedAfterHolidays = new bool[7];

    private RuleParser(string text, List<Token> tokens, bool lenient)
    {
        (_text, _tokens, _lenient) = (text, tokens, lenient);
        _case = lenient ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private enum Kind { Word, Number, Time, Dash, Comma, Semicolon, Space, Plus, Slash, Pipes, Comment, Other, End }

    /// <param name="Position">Where it starts in the text, from 0.</param>
    private readonly record struct Token(Kind Kind, string Text, int Position);

    private Token Peek => _tokens[_index];

    /// <summary>Each day's hours merged, by <see cref="DayOfWeek"/>, and the holidays' when the text states them.</summary>
    /// <exception cref="FormatException">The text isn't hours this reads.</exception>
    public static (ReadOnlyCollection<TimeRange>[] Days, ReadOnlyCollection<TimeRange>? Holidays) Parse(string text, bool lenient)
    {
        var tokens = Tokenize(text, lenient);
        RefuseUnsupported(tokens);
        var parser = new RuleParser(text, tokens, lenient);
        parser.ParseRules();
        return parser.Result();
    }

    /// <summary>Words, numbers, "HH:MM" times and punctuation. Spaces are tokens only when strict, where they must be just so.</summary>
    private static List<Token> Tokenize(string text, bool lenient)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            Kind kind;
            if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (!lenient)
                {
                    tokens.Add(new Token(Kind.Space, text[start..i], start));
                }

                continue;
            }

            if (char.IsLetter(c))
            {
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                kind = Kind.Word;
            }
            else if (char.IsAsciiDigit(c))
            {
                i = SkipDigits(text, i);
                kind = Kind.Number;
                if (i + 1 < text.Length && text[i] == ':' && char.IsAsciiDigit(text[i + 1]))
                {
                    i = SkipDigits(text, i + 1);
                    kind = Kind.Time;
                }
            }
            else if (c == '|' && i + 1 < text.Length && text[i + 1] == '|')
            {
                i += 2;
                kind = Kind.Pipes;
            }
            else if (c == '"')
            {
                var close = text.IndexOf('"', i + 1);
                i = close < 0 ? text.Length : close + 1;
                kind = Kind.Comment;
            }
            else
            {
                i++;
                kind = c switch
                {
                    '-' => Kind.Dash,
                    '–' or '—' when lenient => Kind.Dash,
                    ',' => Kind.Comma,
                    ';' => Kind.Semicolon,
                    '+' => Kind.Plus,
                    '/' => Kind.Slash,
                    _ => Kind.Other,
                };
            }

            tokens.Add(new Token(kind, text[start..i], start));
        }

        tokens.Add(new Token(Kind.End, "", text.Length));
        return tokens;
    }

    private static int SkipDigits(string text, int i)
    {
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Refuses what a station's hours can't be told from, wherever it is, before the grammar's own errors: these say more.
    /// </summary>
    private static void RefuseUnsupported(List<Token> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var previous = i > 0 ? tokens[i - 1] : default;
            var reason = tokens[i].Kind switch
            {
                Kind.Comment => "Comments (\"…\") aren't supported.",
                Kind.Pipes => "Fallback rules (\"||\") aren't supported.",
                Kind.Plus when previous.Kind == Kind.Time => $"Open ends (\"{previous.Text}+\") aren't supported: when it closes isn't known.",
                Kind.Plus => "Offsets (\"+\") aren't supported.",
                Kind.Slash when previous.Kind == Kind.Time => "Repeating times (\"/\") aren't supported.",
                Kind.Word => Unsupported(tokens[i].Text),
                _ => null,
            };
            if (reason is not null)
            {
                throw new FormatException(reason);
            }
        }
    }

    private static string? Unsupported(string word) => word.ToLowerInvariant() switch
    {
        "sunrise" or "sunset" or "dawn" or "dusk" => "Sunrise, sunset, dawn and dusk aren't supported: only clock times are.",
        "sh" => "School holidays (SH) aren't supported.",
        "jan" or "feb" or "mar" or "apr" or "may" or "jun" or "jul" or "aug" or "sep" or "oct" or "nov" or "dec" =>
            "Months and dates (\"Dec 25\") aren't supported.",
        "week" => "Weeks aren't supported.",
        "easter" => "Dates (\"easter\") aren't supported: holidays are PH.",
        "open" or "unknown" => $"\"{word}\" isn't supported: the hours are stated, or \"off\".",
        _ => null,
    };

    /// <summary>rule { "; " rule }; when lenient, also a "," before a rule that starts with a day, and a trailing ";".</summary>
    private void ParseRules()
    {
        var afterComma = false;
        while (true)
        {
            ParseRule(afterComma);
            afterComma = false;
            var token = Next();
            switch (token.Kind)
            {
                case Kind.End:
                    return;
                case Kind.Semicolon when Peek.Kind == Kind.End:
                    if (_lenient)
                    {
                        return;
                    }

                    throw Fail("The hours end with \";\".");
                case Kind.Semicolon:
                    if (!_lenient)
                    {
                        ExpectOneSpace("Rules are separated by \"; \": a semicolon, then one space.");
                    }

                    break;
                case Kind.Comma when StartsDays(SolidAt(0)):
                    if (!_lenient)
                    {
                        throw Fail($"Rules are separated by \"; \", not \",\" ({Where(token)}).");
                    }

                    afterComma = true;
                    break;
                default:
                    var solid = token.Kind == Kind.Space ? SolidAt(0) : token;
                    throw StartsDays(solid)
                        ? Fail($"Rules are separated by \"; \": one is missing before \"{solid.Text}\" ({Where(solid)}).")
                        : IsOff(solid) ? FollowsHours(solid) : Unexpected(token);
            }
        }
    }

    /// <summary>"24/7" | [days " "] hours.</summary>
    /// <param name="afterComma">The rule follows a lenient "," rather than a ";".</param>
    private void ParseRule(bool afterComma)
    {
        var first = Peek;
        if (first is { Kind: Kind.Number, Text: "24" } && At(1) is { Kind: Kind.Slash } && At(2) is { Kind: Kind.Number, Text: "7" })
        {
            _index += 3;
            Set(Everyday, holidays: false, [TimeRange.AllDay], afterComma);
            return;
        }

        var (days, holidays) = (Everyday, false);
        if (StartsDays(first))
        {
            (days, holidays) = ParseDays();
            if (SolidAt(0).Kind is Kind.End or Kind.Semicolon)
            {
                var last = _tokens[_index - 1];
                throw Fail($"\"{_text[first.Position..(last.Position + last.Text.Length)]}\" has no hours: add them, or \"off\".");
            }

            if (!_lenient)
            {
                if (Peek.Kind is not (Kind.Space or Kind.Time or Kind.Number or Kind.Word))
                {
                    throw Unexpected(Peek);
                }

                ExpectOneSpace("The days are followed by one space, then the hours.");
            }
        }

        Set(days, holidays, ParseHours(), afterComma);
    }

    /// <summary>A comma list of days ("Mo"), ranges of days ("Mo-Fr", "Fr-Mo" across the weekend) and PH.</summary>
    private (bool[] Days, bool Holidays) ParseDays()
    {
        var days = new bool[7];
        var holidays = false;
        while (true)
        {
            var token = Next();
            if (IsHolidays(token))
            {
                holidays = true;
            }
            else if (Day(token) is { } first)
            {
                var last = first;
                if (Peek.Kind == Kind.Dash)
                {
                    Next();
                    var end = Next();
                    last = Day(end) ?? throw Fail($"A range of days ends with a day: \"{end.Text}\" isn't one ({Where(end)}).");
                }

                for (var day = first; ; day = (DayOfWeek)(((int)day + 1) % 7))
                {
                    days[(int)day] = true;
                    if (day == last)
                    {
                        break;
                    }
                }
            }
            else
            {
                throw Unexpected(token);
            }

            if (Peek.Kind != Kind.Comma || !StartsDays(SolidAt(1)))
            {
                return (days, holidays);
            }

            Next();
            if (Peek.Kind == Kind.Space)
            {
                throw Unexpected(Peek);
            }
        }
    }

    /// <summary>"off" | "closed" | a comma list of "HH:MM-HH:MM".</summary>
    private List<TimeRange> ParseHours()
    {
        if (IsWord(Peek, "off") || IsWord(Peek, "closed"))
        {
            Next();
            return [];
        }

        var ranges = new List<TimeRange>();
        while (true)
        {
            ranges.Add(ParseRange());
            if (Peek.Kind != Kind.Comma || SolidAt(1).Kind != Kind.Time)
            {
                return ranges;
            }

            Next();
            if (Peek.Kind == Kind.Space)
            {
                throw Unexpected(Peek);
            }
        }
    }

    private TimeRange ParseRange()
    {
        var from = Next();
        var start = Minutes(from, end: false);
        if (Next().Kind != Kind.Dash)
        {
            throw Fail($"\"{from.Text}\" has no end: write \"{from.Text}-HH:MM\".");
        }

        var to = Next();
        var end = Minutes(to, end: true);
        if (end == start)
        {
            throw Fail($"\"{from.Text}-{to.Text}\" starts and ends at the same time: all day is \"00:00-24:00\".");
        }

        // An end before the start is on the next day; one at 23:59 is made midnight by OpeningHours.Merge.
        return new TimeRange(start, end < start ? end + TimeRange.MinutesPerDay : end);
    }

    /// <summary>The minutes from midnight of "HH:MM": 24:00 only as an end.</summary>
    private int Minutes(Token token, bool end)
    {
        if (token.Kind == Kind.Number)
        {
            throw Fail($"\"{token.Text}\" isn't a time: times are written HH:MM ({Where(token)}).");
        }

        if (token.Kind != Kind.Time)
        {
            throw Unexpected(token);
        }

        var colon = token.Text.IndexOf(':');
        var (hours, minutes) = (token.Text[..colon], token.Text[(colon + 1)..]);
        if (hours.Length > 2 || minutes.Length != 2)
        {
            throw Fail($"\"{token.Text}\" isn't a time: times are written HH:MM.");
        }

        if (hours.Length == 1 && !_lenient)
        {
            throw Fail($"\"{token.Text}\" has a one-digit hour: write \"0{token.Text}\".");
        }

        var (hour, minute) = (int.Parse(hours, CultureInfo.InvariantCulture), int.Parse(minutes, CultureInfo.InvariantCulture));
        if (minute >= 60)
        {
            throw Fail($"\"{token.Text}\" isn't a time: minutes go up to 59.");
        }

        if (hour > 24 || hour == 24 && minute > 0)
        {
            throw Fail($"\"{token.Text}\" isn't a time: the day ends at 24:00.");
        }

        if (hour == 24 && !end)
        {
            throw Fail($"\"{token.Text}\" can only end a range.");
        }

        return hour * 60 + minute;
    }

    /// <summary>
    /// A later rule replaces the hours of the days it names, as in OpenStreetMap, and so of the holidays that fall on them
    /// (<see cref="Result"/> sorts that out). Unlike OpenStreetMap, it leaves the day before's spill past midnight alone.
    /// </summary>
    private void Set(bool[] days, bool holidays, List<TimeRange> ranges, bool afterComma)
    {
        // OpenStreetMap's "," adds a rule's hours to a day's, where ";" replaces them: a source that wrote "," for ";"
        // means the same either way (a spill into the next day is kept by both here), unless the rule names a day again.
        // Then which it meant can't be told.
        for (var i = 0; afterComma && i < _set.Length; i++)
        {
            if (_set[i] is not null && (i == Holidays ? holidays : days[i]))
            {
                var name = i == Holidays ? "PH" : OpeningHours.DayNames[i];
                throw Fail($"{name} is named again after a \",\", which may add to its hours or replace them: write \";\" to replace them.");
            }
        }

        // Naming days after PH names again the holidays that fall on them: that matters only when PH's hours aren't "off".
        if (afterComma && !holidays && _set[Holidays] is [_, ..] && days.Contains(true))
        {
            throw Fail($"A rule for {Names(days)} comes after PH's hours and a \",\", which may add to the hours of holidays that fall then or replace them: put PH last.");
        }

        for (var day = 0; day < days.Length; day++)
        {
            if (days[day])
            {
                _set[day] = ranges;
            }
        }

        if (holidays)
        {
            _set[Holidays] = ranges;
            Array.Clear(_namedAfterHolidays);
        }
        else if (_set[Holidays] is not null)
        {
            for (var day = 0; day < days.Length; day++)
            {
                _namedAfterHolidays[day] |= days[day];
            }
        }
    }

    private (ReadOnlyCollection<TimeRange>[] Days, ReadOnlyCollection<TimeRange>? Holidays) Result()
    {
        var days = new ReadOnlyCollection<TimeRange>[7];
        for (var day = 0; day < days.Length; day++)
        {
            days[day] = _set[day] is { } ranges
                ? OpeningHours.Merge(ranges) ?? throw Fail($"{OpeningHours.DayNames[day]}'s hours run past midnight into more than a whole day.")
                : ReadOnlyCollection<TimeRange>.Empty;
        }

        var holidays = _set[Holidays] is { } holidayRanges
            ? OpeningHours.Merge(holidayRanges) ?? throw Fail("PH's hours run past midnight into more than a whole day.")
            : null;

        // A holiday on a day named after PH has that day's hours. When the days left alone have PH's hours anyway, every
        // holiday has its day's, which is no PH at all ("PH off; Mo-Su 06:00-22:00"); otherwise PH holds on some
        // holidays and not on others, which one set of holiday hours can't say.
        if (holidays is not null && _namedAfterHolidays.Contains(true))
        {
            for (var day = 0; day < days.Length; day++)
            {
                if (!_namedAfterHolidays[day] && !days[day].SequenceEqual(holidays))
                {
                    throw Fail($"PH's hours come before those of {Names(_namedAfterHolidays)}, which replace them on the holidays that fall then: put PH last.");
                }
            }

            holidays = null;
        }

        return (days, holidays);
    }

    /// <summary>The days, by <see cref="DayOfWeek"/>, as runs Monday first: "Mo-Fr", "Mo,We-Th".</summary>
    private static string Names(bool[] days)
    {
        var runs = new List<string>();
        for (var i = 0; i < OpeningHours.Week.Length; i++)
        {
            if (!days[(int)OpeningHours.Week[i]])
            {
                continue;
            }

            var last = i;
            while (last + 1 < OpeningHours.Week.Length && days[(int)OpeningHours.Week[last + 1]])
            {
                last++;
            }

            runs.Add(OpeningHours.Days(OpeningHours.Week[i], OpeningHours.Week[last]));
            i = last;
        }

        return string.Join(",", runs);
    }

    private Token Next()
    {
        var token = _tokens[_index];
        if (token.Kind != Kind.End)
        {
            _index++;
        }

        return token;
    }

    private Token At(int offset) => _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

    /// <summary>The first token that isn't a space, from <paramref name="offset"/> tokens ahead.</summary>
    private Token SolidAt(int offset)
    {
        var i = Math.Min(_index + offset, _tokens.Count - 1);
        while (_tokens[i].Kind == Kind.Space)
        {
            i++;
        }

        return _tokens[i];
    }

    private void ExpectOneSpace(string message)
    {
        if (Peek is not { Kind: Kind.Space, Text: " " })
        {
            throw Fail(message);
        }

        Next();
    }

    private DayOfWeek? Day(Token token)
    {
        if (token.Kind == Kind.Word)
        {
            for (var day = 0; day < OpeningHours.DayNames.Length; day++)
            {
                if (string.Equals(token.Text, OpeningHours.DayNames[day], _case))
                {
                    return (DayOfWeek)day;
                }
            }
        }

        return null;
    }

    private bool IsHolidays(Token token) => IsWord(token, "PH");

    private bool StartsDays(Token token) => Day(token) is not null || IsHolidays(token);

    private bool IsWord(Token token, string word) => token.Kind == Kind.Word && string.Equals(token.Text, word, _case);

    private FormatException Unexpected(Token token) => token.Kind switch
    {
        Kind.End => Fail("The hours end too soon."),
        Kind.Space => Fail($"Unexpected space ({Where(token)}): spaces go only after \";\" and between the days and the hours."),
        Kind.Word when StartsDays(token) => Fail($"Unexpected \"{token.Text}\" ({Where(token)}): a rule's days come before its hours."),
        Kind.Word when OpeningHours.DayNames.Contains(token.Text, StringComparer.OrdinalIgnoreCase) || token.Text.Equals("PH", StringComparison.OrdinalIgnoreCase) =>
            Fail($"Days are written Mo, Tu, We, Th, Fr, Sa, Su and PH: \"{token.Text}\" isn't one."),
        Kind.Word when IsOff(token) => !_lenient && token.Text is not ("off" or "closed")
            ? Fail($"\"{token.Text}\" is written in lowercase.")
            : FollowsHours(token),
        Kind.Number => Fail($"\"{token.Text}\" isn't supported ({Where(token)}): dates, years and week numbers aren't."),
        Kind.Other when token.Text is "–" or "—" => Fail($"Ranges are written with \"-\", not \"{token.Text}\" ({Where(token)})."),
        _ => Fail($"Unexpected \"{token.Text}\" ({Where(token)})."),
    };

    /// <summary>"off" or "closed", in any case.</summary>
    private static bool IsOff(Token token) =>
        token.Kind == Kind.Word && (token.Text.Equals("off", StringComparison.OrdinalIgnoreCase) || token.Text.Equals("closed", StringComparison.OrdinalIgnoreCase));

    /// <summary>For "off" where it doesn't go: after the hours ("Mo-Fr 12:00-13:00 off"), or as a time.</summary>
    private static FormatException FollowsHours(Token token) =>
        Fail($"Unexpected \"{token.Text}\" ({Where(token)}): \"{token.Text}\" takes the place of a rule's hours, it doesn't follow them.");

    private static string Where(Token token) => $"at character {token.Position + 1}";

    private static FormatException Fail(string message) => new(message);
}
