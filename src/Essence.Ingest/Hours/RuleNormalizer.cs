using System.Globalization;
using System.Text.RegularExpressions;
using Essence.Hours;

namespace Essence.Ingest.Hours;

/// <summary>One line of a brand's week as its locator writes it: "Mon-Fri." with "6:00am - 9:30pm".</summary>
/// <param name="Code">What some locators add: "open" or "closed"; null when there's none.</param>
public sealed record WeekLine(string Title, string? Code, string Time);

/// <summary>
/// Puts the hours brands' locators write in English (days as "Mon-Fri.", "Sat." or "monday", times as "6:00am - 9:30pm",
/// "closed", or 24-hour clock times "05:30" to "00:30") into a canonical opening_hours rule. Only what can be told for
/// sure is read: anything else is refused with the reason, and the record is left out.
/// </summary>
public static partial class RuleNormalizer
{
    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.Ordinal)
    {
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
    };

    /// <summary>
    /// The week's rule, every day given exactly once: "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30". A day left out isn't taken
    /// as closed, since the locator may just not say.
    /// </summary>
    /// <param name="error">Why the lines were refused.</param>
    public static string? Week(IEnumerable<WeekLine> lines, out string? error)
    {
        var week = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>();
        foreach (var line in lines)
        {
            if (!TryDays(line.Title, out var days))
            {
                error = $"the days \"{line.Title.Trim()}\" aren't understood";
                return null;
            }

            if (!TryTimes(line.Code, line.Time, out var ranges))
            {
                error = $"the hours \"{line.Time.Trim()}\"{(line.Code is { } code ? $" ({code})" : "")} aren't understood";
                return null;
            }

            foreach (var day in days)
            {
                if (!week.TryAdd(day, ranges))
                {
                    error = $"{day}'s hours are given twice";
                    return null;
                }
            }
        }

        return Week(week, out error);
    }

    /// <summary>
    /// The week's rule from each day's hours (none for a day it's closed), every day given: "Mo-Su 06:00-22:00". A day
    /// missing isn't taken as closed, since the source may just not say.
    /// </summary>
    /// <param name="error">Why the week was refused.</param>
    public static string? Week(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TimeRange>> week, out string? error)
    {
        if (week.Count < 7)
        {
            error = $"{Enum.GetValues<DayOfWeek>().First(d => !week.ContainsKey(d))}'s hours aren't given";
            return null;
        }

        try
        {
            error = null;
            return OpeningHours.FromDays(week).ToRule();
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>"Mon-Fri.", "Mon.-Fri.", "Sat.", "Mon-Sun.", "Thursday", or a range across the weekend ("Sat-Mon").</summary>
    public static bool TryDays(string title, out IReadOnlyList<DayOfWeek> days)
    {
        days = [];
        var parts = title.Replace(".", "", StringComparison.Ordinal).Replace('–', '-').Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !TryDay(parts[0], out var first) || !TryDay(parts[^1], out var last))
        {
            return false;
        }

        days = Range(first, last);
        return true;
    }

    /// <summary>A day's English name or its abbreviation, in any case: "Mon", "monday", "Thurs".</summary>
    public static bool TryDay(string? name, out DayOfWeek day)
    {
        day = default;
        return name is not null && DayNames.TryGetValue(name.Trim().ToLowerInvariant(), out day);
    }

    /// <summary>The days from <paramref name="first"/> to <paramref name="last"/>, across the weekend when it comes before: Sat to Mon is Sa Su Mo.</summary>
    public static IReadOnlyList<DayOfWeek> Range(DayOfWeek first, DayOfWeek last)
    {
        var list = new List<DayOfWeek> { first };
        for (var day = first; day != last;)
        {
            day = (DayOfWeek)(((int)day + 1) % 7);
            list.Add(day);
        }

        return list;
    }

    /// <summary>
    /// A range in 24-hour clock times: "06:00" to "23:00". An end at or before the start is past midnight ("05:30" to
    /// "00:30"); "00:00" and "24:00" as an end are midnight, so "00:00" to "24:00" is the whole day, and "23:59" is read as
    /// midnight later, as Essence.Hours reads it. The same start and end ("06:00" to "06:00") isn't a range.
    /// </summary>
    public static bool TryRange(string? open, string? close, out TimeRange range)
    {
        range = default;
        if (!TryClock24(open, end: false, out var start) || !TryClock24(close, end: true, out var end))
        {
            return false;
        }

        range = new TimeRange(start, end <= start ? end + TimeRange.MinutesPerDay : end);
        return range.IsValid;
    }

    /// <summary>
    /// "6:00am - 9:30pm" (an end before the start is past midnight, "12:00am" at the end is midnight), several ranges
    /// separated by commas, or "closed". With a <paramref name="code"/>, it must agree: "open" with times, "closed" with
    /// "closed" or nothing.
    /// </summary>
    /// <param name="ranges">Empty when it's closed.</param>
    public static bool TryTimes(string? code, string time, out IReadOnlyList<TimeRange> ranges)
    {
        ranges = [];
        var text = time.Trim();
        var normalizedCode = code?.Trim().ToLowerInvariant();
        var closed = text.Equals("closed", StringComparison.OrdinalIgnoreCase);
        if (normalizedCode is "closed")
        {
            return closed || text.Length == 0;
        }

        if (normalizedCode is not (null or "open"))
        {
            return false;
        }

        if (closed)
        {
            return normalizedCode is null;
        }

        var list = new List<TimeRange>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var match = RangePattern().Match(part);
            if (!match.Success || !TryClock(match.Groups["start"].Value, end: false, out var start) || !TryClock(match.Groups["end"].Value, end: true, out var end))
            {
                return false;
            }

            var range = new TimeRange(start, end <= start ? end + TimeRange.MinutesPerDay : end);
            if (!range.IsValid)
            {
                return false;
            }

            list.Add(range);
        }

        ranges = list;
        return true;
    }

    /// <summary>"6:00am", "9:30 p.m.", "6am": minutes from midnight; "12:00am" is 0 as a start and 1440 (24:00) as an end.</summary>
    private static bool TryClock(string text, bool end, out int minute)
    {
        minute = 0;
        var match = ClockPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minutes = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minutes > 59)
        {
            return false;
        }

        var pm = char.ToLowerInvariant(match.Groups["half"].Value[0]) == 'p';
        minute = (hour % 12 + (pm ? 12 : 0)) * 60 + minutes;
        if (end && minute == 0)
        {
            minute = TimeRange.MinutesPerDay;
        }

        return true;
    }

    /// <summary>"05:30", "24:00": minutes from midnight; "00:00" as an end is 1440 (24:00), and "24:00" only an end.</summary>
    private static bool TryClock24(string? text, bool end, out int minute)
    {
        minute = 0;
        if (text is null || Clock24Pattern().Match(text.Trim()) is not { Success: true } match)
        {
            return false;
        }

        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        if (hour > 24 || minutes > 59 || hour == 24 && (minutes > 0 || !end))
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

    [GeneratedRegex(@"^(?<start>[^-–]+?)\s*[-–]\s*(?<end>[^-–]+)$")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^(?<hour>[0-9]{1,2}):(?<minute>[0-9]{2})$")]
    private static partial Regex Clock24Pattern();

    [GeneratedRegex(@"^(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<half>[ap])\.?\s*m\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClockPattern();
}
