namespace Essence.Hours;

/// <summary>Whether a station is open at a moment, and when that changes.</summary>
/// <param name="ChangesAt">
/// When it closes if it's open, or opens if it's closed, in the station's time; null when that's past
/// <see cref="HoursEvaluator.DaysAhead"/> days (open all the time, or not opening that soon).
/// </param>
/// <param name="IsHoliday">The station's day is a public holiday (<see cref="QuebecHolidays.IsPublicHoliday"/>).</param>
/// <param name="HolidayCaution">
/// The day's hours may not be the usual ones, and the rule doesn't say: a public holiday whose hours it doesn't state, or
/// a day stores' hours are restricted (<see cref="QuebecHolidays.IsCautionDay"/>). Never for a station open 24/7.
/// </param>
public sealed record HoursState(bool IsOpen, DateTimeOffset? ChangesAt, bool IsHoliday, bool HolidayCaution);

/// <summary>What the driver finds on getting to a station.</summary>
public enum Arrival
{
    Open,

    /// <summary>Open, but it closes soon after: the driver may not make it.</summary>
    ClosesSoon,

    Closed,
}

/// <param name="ClosesAt">When it closes, when it's open on arrival and closes within <see cref="HoursEvaluator.DaysAhead"/> days.</param>
/// <param name="OpensAt">When it next opens: after the arrival when it's closed then, after it closes when it's open.</param>
/// <param name="HolidayCaution">As <see cref="HoursState.HolidayCaution"/>, on the day of the arrival.</param>
public sealed record ArrivalCheck(Arrival State, DateTimeOffset? ClosesAt, DateTimeOffset? OpensAt, bool HolidayCaution);

/// <summary>
/// A station's hours against the clock, in its time zone. The days around the moment are laid out as stretches of time,
/// a public holiday with its own hours when the rule states them, and stretches that meet are one: open until 24:00 and
/// from 00:00 the next day doesn't close at midnight, and 24/7 never closes.
/// </summary>
public static class HoursEvaluator
{
    /// <summary>The days after the moment that are laid out: a station open once a week still has its next opening.</summary>
    public const int DaysAhead = 8;

    /// <summary>Whether the station is open at <paramref name="at"/>, and when that changes.</summary>
    public static HoursState At(OpeningHours hours, TimeZoneInfo zone, DateTimeOffset at)
    {
        var (day, current, next) = Locate(hours, zone, at);
        var isHoliday = QuebecHolidays.IsPublicHoliday(day);
        return new HoursState(current is not null, current is { } open ? open.Closes : next?.Start, isHoliday, Caution(hours, day, isHoliday));
    }

    /// <summary>
    /// Whether the station is open when the driver gets there: <see cref="Arrival.ClosesSoon"/> when it closes at or before
    /// <paramref name="closesSoonWithin"/> after the arrival.
    /// </summary>
    public static ArrivalCheck OnArrival(OpeningHours hours, TimeZoneInfo zone, DateTimeOffset arriveAt, TimeSpan closesSoonWithin)
    {
        var (day, current, next) = Locate(hours, zone, arriveAt);
        var caution = Caution(hours, day, QuebecHolidays.IsPublicHoliday(day));
        if (current is not { } open)
        {
            return new ArrivalCheck(Arrival.Closed, null, next?.Start, caution);
        }

        var closesSoon = open.Closes is { } closes && closes <= arriveAt + closesSoonWithin;
        return new ArrivalCheck(closesSoon ? Arrival.ClosesSoon : Arrival.Open, open.Closes, next?.Start, caution);
    }

    /// <summary>
    /// The instant of a local time, never failing: one in the spring-forward gap moves forward by the gap (02:30 is 03:30
    /// daylight time), and one the fall-back repeats is the first, in daylight time.
    /// </summary>
    internal static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(local)
            // Read with the offset from before the gap, it lands as far past the gap as it was into it.
            ? zone.GetUtcOffset(local.AddDays(-1))
            : zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).Max()
                : zone.GetUtcOffset(local);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(local, offset), zone);
    }

    /// <summary>The station's day at the moment, the stretch open then if any, and the next one after.</summary>
    private static (DateOnly Day, Stretch? Current, Stretch? Next) Locate(OpeningHours hours, TimeZoneInfo zone, DateTimeOffset at)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var horizon = today.AddDays(DaysAhead + 1).ToDateTime(TimeOnly.MinValue);

        // From the day before, whose hours can spill into today, to the last day laid out.
        var stretches = new List<Stretch>();
        for (var offset = -1; offset <= DaysAhead; offset++)
        {
            var day = today.AddDays(offset);
            var midnight = day.ToDateTime(TimeOnly.MinValue);
            var ranges = hours.OnHolidays is { } holidays && QuebecHolidays.IsPublicHoliday(day) ? holidays : hours.On(day.DayOfWeek);
            foreach (var range in ranges)
            {
                var end = midnight.AddMinutes(range.EndMinute);
                stretches.Add(new Stretch(Instant(midnight.AddMinutes(range.StartMinute), zone), Instant(end, zone), end >= horizon));
            }
        }

        // Merged once they're instants: a stretch that closes in the spring-forward gap moves an hour later, past the
        // start of one that opens just after the gap, and one that opens in the gap and closes just after it would close
        // before it opens: it's left out.
        stretches.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<Stretch>();
        foreach (var stretch in stretches.Where(s => s.Start < s.End))
        {
            if (merged.Count > 0 && stretch.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = stretch.End > last.End ? last with { End = stretch.End, PastHorizon = stretch.PastHorizon } : last;
            }
            else
            {
                merged.Add(stretch);
            }
        }

        Stretch? current = null;
        foreach (var stretch in merged)
        {
            if (stretch.Start > at)
            {
                return (today, current, stretch);
            }

            if (at < stretch.End)
            {
                current = stretch;
            }
        }

        return (today, current, null);
    }

    private static bool Caution(OpeningHours hours, DateOnly day, bool isHoliday) =>
        QuebecHolidays.IsCautionDay(day) && !hours.IsAlwaysOpen && (!isHoliday || hours.OnHolidays is null);

    /// <param name="PastHorizon">It's still open at the end of the days laid out: when it closes isn't known.</param>
    private readonly record struct Stretch(DateTimeOffset Start, DateTimeOffset End, bool PastHorizon)
    {
        public DateTimeOffset? Closes => PastHorizon ? null : End;
    }
}
