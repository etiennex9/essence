namespace Essence.Hours;

/// <summary>Québec's time zones, which a station's hours are in.</summary>
public static class QuebecTime
{
    /// <summary>Most of Québec: Eastern time, with daylight time (America/Toronto, which Montréal follows).</summary>
    public static TimeZoneInfo Eastern { get; } = Find("America/Toronto", TimeSpan.FromHours(-5), daylightTime: true);

    /// <summary>The Îles-de-la-Madeleine: Atlantic time, with daylight time (America/Halifax).</summary>
    public static TimeZoneInfo Atlantic { get; } = Find("America/Halifax", TimeSpan.FromHours(-4), daylightTime: true);

    /// <summary>The Lower North Shore, from Kegaska to Blanc-Sablon: Atlantic standard time all year (America/Blanc-Sablon).</summary>
    public static TimeZoneInfo LowerNorthShore { get; } = Find("America/Blanc-Sablon", TimeSpan.FromHours(-4), daylightTime: false);

    /// <summary>
    /// The zone a station is in, by where it is: the islands are east of 62.5° W and south of 48.5° N (Cap-aux-Meules is at
    /// 47.38, -61.86); the Lower North Shore east of 61.5° W and north of 50° N, so Natashquan (50.19, -61.81), Aguanish and
    /// Baie-Johan-Beetz, which keep Eastern time, stay out of it. Without coordinates, Eastern.
    /// </summary>
    public static TimeZoneInfo ZoneFor(double? latitude, double? longitude) => (latitude, longitude) switch
    {
        ({ } lat, { } lon) when lon > -62.5 && lat < 48.5 => Atlantic,
        ({ } lat, { } lon) when lon >= -61.5 && lat >= 50 => LowerNorthShore,
        _ => Eastern,
    };

    /// <summary>
    /// The system's zone, or where the platform has no time-zone data (a trimmed WebAssembly app, a bare container) one
    /// built from Canada's rule: <see cref="Canadian"/>.
    /// </summary>
    private static TimeZoneInfo Find(string id, TimeSpan standardOffset, bool daylightTime)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Canadian(id, standardOffset, daylightTime);
        }
    }

    /// <summary>
    /// A zone that follows Canada's rule since 2007: daylight time, an hour ahead, from the second Sunday of March at 02:00
    /// to the first Sunday of November at 02:00. Before 2007 it has none, which nothing here looks at.
    /// </summary>
    internal static TimeZoneInfo Canadian(string id, TimeSpan standardOffset, bool daylightTime)
    {
        if (!daylightTime)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, standardOffset, id, id);
        }

        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2007, 1, 1),
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone(id, standardOffset, id, id, id, [rule]);
    }
}
