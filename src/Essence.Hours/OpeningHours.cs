using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Essence.Hours;

/// <summary>Days in a row with the same hours, for showing a station's week.</summary>
/// <param name="Ranges">Empty when it's closed on those days.</param>
public sealed record DayGroup(DayOfWeek First, DayOfWeek Last, IReadOnlyList<TimeRange> Ranges)
{
    /// <summary>The same days and the same hours: the ranges are compared one by one, not as lists.</summary>
    public bool Equals(DayGroup? other) =>
        other is not null && First == other.First && Last == other.Last && Ranges.SequenceEqual(other.Ranges);

    public override int GetHashCode() => HashCode.Combine(First, Last, Ranges.Count);

    /// <summary>"Mo-Fr 06:00-21:30", "Su off".</summary>
    public override string ToString() => $"{OpeningHours.Days(First, Last)} {OpeningHours.Times(Ranges)}";
}

/// <summary>
/// A station's week: each day's hours, and its public holidays' when the source states them. Every source's hours are
/// put in OpenStreetMap's opening_hours, which this reads (<see cref="TryParse"/>) and writes (<see cref="ToRule"/>),
/// limited to what a station's hours need: days, public holidays and clock times. Two are equal when their schedules
/// are, however they were written.
/// </summary>
public sealed class OpeningHours : IEquatable<OpeningHours>
{
    /// <summary>The week as it's shown, Monday first.</summary>
    internal static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>opening_hours' names for the days, by <see cref="DayOfWeek"/>.</summary>
    internal static readonly string[] DayNames = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

    private static readonly ReadOnlyCollection<TimeRange> AllDayRanges = new([TimeRange.AllDay]);

    // By DayOfWeek, each sorted with its overlaps merged.
    private readonly ReadOnlyCollection<TimeRange>[] _days;
    private readonly ReadOnlyCollection<TimeRange>? _holidays;

    private OpeningHours(ReadOnlyCollection<TimeRange>[] days, ReadOnlyCollection<TimeRange>? holidays)
    {
        _days = days;

        // Open all day on holidays says nothing more when it's open all day every day: dropped, "24/7" is the same.
        _holidays = holidays is [var only] && only == TimeRange.AllDay && days.All(d => d is [var day] && day == TimeRange.AllDay)
            ? null
            : holidays;
    }

    /// <summary>"24/7".</summary>
    public static OpeningHours AlwaysOpen { get; } = new([.. Enumerable.Repeat(AllDayRanges, 7)], null);

    /// <summary>Open 00:00-24:00 every day, holidays included: nothing ever closes it.</summary>
    public bool IsAlwaysOpen =>
        _days.All(d => d is [var range] && range == TimeRange.AllDay) && (_holidays is null || _holidays is [var h] && h == TimeRange.AllDay);

    /// <summary>
    /// The hours on public holidays (<see cref="QuebecHolidays.IsPublicHoliday"/>), instead of the day's own: null when the
    /// rule doesn't state them (a holiday then has its day's hours, which may not hold), empty for "PH off".
    /// </summary>
    public IReadOnlyList<TimeRange>? OnHolidays => _holidays;

    /// <summary>
    /// Reads opening_hours: "24/7", or rules separated by "; ", each <c>[days] hours</c>. The days are a comma list of
    /// Mo Tu We Th Fr Sa Su, ranges of them ("Mo-Fr", or "Fr-Mo" across the weekend) and PH; no days means every day. The
    /// hours are "off", "closed", or a comma list of "HH:MM-HH:MM": an end of "24:00" or "23:59" is midnight, and an end
    /// before the start is on the next day. Days a rule doesn't name are closed, and a later rule replaces the hours of
    /// the days it names, as in OpenStreetMap, holidays that fall on them included: "PH off; Mo-Su 06:00-22:00" is open on
    /// holidays. PH goes last: before a rule that would leave its hours on only some holidays, it's refused.
    /// <para>
    /// Unlike OpenStreetMap, a range past midnight keeps its next-day part when a later rule names that day: "Sa
    /// 16:00-03:00; Su 16:00-23:00" is open early on Sunday, as its writer surely meant (OpenStreetMap's own wiki calls its
    /// reading, closed then, probably not intended). Each day's hours stay on the day they start.
    /// </para>
    /// </summary>
    /// <param name="lenient">
    /// Also takes what sources get wrong when they mean the same: a "," between rules where the next one starts with a
    /// day, one-digit hours ("6:00"), days in any case ("mo-fr"), any spacing, an en or em dash for "-", and a trailing ";".
    /// </param>
    /// <param name="error">Why it was refused. What can't be told for sure (an open end, sunset, a comment, a date,
    /// school holidays, a fallback rule…) is refused rather than guessed.</param>
    public static bool TryParse(
        string? text,
        bool lenient,
        [NotNullWhen(true)] out OpeningHours? hours,
        [NotNullWhen(false)] out string? error)
    {
        hours = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "There are no hours.";
            return false;
        }

        try
        {
            var (days, holidays) = RuleParser.Parse(text, lenient);
            hours = new OpeningHours(days, holidays);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <inheritdoc cref="TryParse"/>
    /// <exception cref="FormatException">The text isn't hours this reads; the message says why.</exception>
    public static OpeningHours Parse(string text, bool lenient = false) =>
        TryParse(text, lenient, out var hours, out var error) ? hours : throw new FormatException(error);

    /// <summary>A week as the station's editor sets it. Days left out are closed, and an end of 23:59 is midnight, as when parsed.</summary>
    /// <param name="holidays">The hours on public holidays, or null to keep each holiday's day's hours.</param>
    /// <exception cref="ArgumentException">A range isn't <see cref="TimeRange.IsValid"/>, or a day's run into more than 24 hours past midnight.</exception>
    public static OpeningHours FromDays(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<TimeRange>> days, IReadOnlyList<TimeRange>? holidays = null)
    {
        var week = new ReadOnlyCollection<TimeRange>[7];
        foreach (var day in Week)
        {
            week[(int)day] = days.TryGetValue(day, out var ranges)
                ? Merge(ranges) ?? throw new ArgumentException($"{DayNames[(int)day]}'s hours run past midnight into more than a whole day.", nameof(days))
                : ReadOnlyCollection<TimeRange>.Empty;
        }

        return new OpeningHours(
            week,
            holidays is null ? null : Merge(holidays) ?? throw new ArgumentException("The holidays' hours run past midnight into more than a whole day.", nameof(holidays)));
    }

    /// <summary>The day's own ranges, sorted; a range that spills past midnight is on the day it starts.</summary>
    public IReadOnlyList<TimeRange> On(DayOfWeek day) => _days[(int)day];

    /// <summary>The week for showing it: days in a row with the same hours, Monday first, closed days included.</summary>
    public IReadOnlyList<DayGroup> Groups()
    {
        var groups = new List<DayGroup>();
        for (var i = 0; i < Week.Length;)
        {
            var ranges = _days[(int)Week[i]];
            var last = i;
            while (last + 1 < Week.Length && _days[(int)Week[last + 1]].SequenceEqual(ranges))
            {
                last++;
            }

            groups.Add(new DayGroup(Week[i], Week[last], ranges));
            i = last + 1;
        }

        return groups;
    }

    /// <summary>
    /// The canonical opening_hours: "24/7" when always open; otherwise the open days in runs with the same hours, Monday
    /// first ("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30"), the closed ones left out ("Mo-Su off" when it's never open), then
    /// the holidays when stated ("PH off"). <see cref="Parse"/> reads it back to an equal schedule.
    /// <para>
    /// It's written for this library to read: where a range spills past midnight into a day with hours of its own
    /// ("Mo-Fr 22:00-02:00; Sa 10:00-20:00"), OpenStreetMap's readers cut the spill short (see <see cref="TryParse"/>).
    /// </para>
    /// </summary>
    public string ToRule()
    {
        if (IsAlwaysOpen)
        {
            return "24/7";
        }

        var rules = Groups().Where(g => g.Ranges.Count > 0).Select(g => g.ToString()).ToList();
        if (rules.Count == 0)
        {
            rules.Add("Mo-Su off");
        }

        if (_holidays is not null)
        {
            rules.Add($"PH {Times(_holidays)}");
        }

        return string.Join("; ", rules);
    }

    public override string ToString() => ToRule();

    public bool Equals(OpeningHours? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || _days.Zip(other._days).All(d => d.First.SequenceEqual(d.Second))
            && (_holidays is null ? other._holidays is null : other._holidays is not null && _holidays.SequenceEqual(other._holidays)));

    public override bool Equals(object? obj) => Equals(obj as OpeningHours);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var day in _days)
        {
            hash.Add(day.Count);
            foreach (var range in day)
            {
                hash.Add(range);
            }
        }

        hash.Add(_holidays?.Count ?? -1);
        return hash.ToHashCode();
    }

    public static bool operator ==(OpeningHours? left, OpeningHours? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(OpeningHours? left, OpeningHours? right) => !(left == right);

    /// <summary>
    /// The ranges sorted, with those that overlap or touch made one ("06:00-12:00,12:00-18:00" is "06:00-18:00"), and an
    /// end of 23:59 made 24:00, since some sources mean the end of the day by it: parsed or from the editor, a range is
    /// read the same, and "23:59" is never written. Null when that makes a range that can't be written: all day and past
    /// midnight, more than 24 hours.
    /// </summary>
    /// <exception cref="ArgumentException">A range isn't <see cref="TimeRange.IsValid"/>.</exception>
    internal static ReadOnlyCollection<TimeRange>? Merge(IEnumerable<TimeRange> ranges)
    {
        var merged = new List<TimeRange>();
        foreach (var given in ranges.OrderBy(r => r.StartMinute))
        {
            if (!given.IsValid)
            {
                throw new ArgumentException($"{given.StartMinute}..{given.EndMinute} isn't a range of a day's minutes.", nameof(ranges));
            }

            var range = given.EndMinute == TimeRange.MinutesPerDay - 1 ? given with { EndMinute = TimeRange.MinutesPerDay } : given;
            if (merged.Count > 0 && range.StartMinute <= merged[^1].EndMinute)
            {
                merged[^1] = merged[^1] with { EndMinute = Math.Max(merged[^1].EndMinute, range.EndMinute) };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged.All(r => r.IsValid) ? merged.AsReadOnly() : null;
    }

    /// <summary>"Mo", or "Mo-Fr".</summary>
    internal static string Days(DayOfWeek first, DayOfWeek last) =>
        first == last ? DayNames[(int)first] : $"{DayNames[(int)first]}-{DayNames[(int)last]}";

    /// <summary>"06:00-12:00,13:00-17:00", or "off".</summary>
    internal static string Times(IReadOnlyList<TimeRange> ranges) => ranges.Count == 0 ? "off" : string.Join(",", ranges);
}
