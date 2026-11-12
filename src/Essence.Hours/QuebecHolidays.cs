using System.Collections.Concurrent;

namespace Essence.Hours;

/// <summary>A day a station's usual hours may not hold.</summary>
/// <param name="Key">What it is, for its name in the app: "new-year", "fete-nationale"…; a holiday moved off a Sunday keeps its key.</param>
/// <param name="Public">
/// A public holiday, what opening_hours' PH means here. Otherwise a day when stores' hours are restricted, which some
/// stations follow.
/// </param>
public sealed record Holiday(DateOnly Day, string Key, bool Public);

/// <summary>Québec's public holidays, as the CNESST lists them, and the days retail hours are restricted.</summary>
public static class QuebecHolidays
{
    private static readonly ConcurrentDictionary<int, IReadOnlyList<Holiday>> Years = new();

    /// <summary>
    /// The year's holidays, by date. Public: January 1; Good Friday and Easter Monday (the employer picks one, so both
    /// count); the Journée nationale des patriotes, the Monday before May 25; June 24, and June 25 when the 24th is a
    /// Sunday; July 1, and July 2 when the 1st is a Sunday; Labour Day, the first Monday of September; Thanksgiving, the
    /// second Monday of October; December 25. Not public, but stores' hours are restricted: Easter Sunday, December 26 and
    /// January 2.
    /// </summary>
    public static IReadOnlyList<Holiday> For(int year) => Years.GetOrAdd(year, Compute);

    /// <summary>A public holiday: what opening_hours' PH means for a station in Québec.</summary>
    public static bool IsPublicHoliday(DateOnly day) => For(day.Year).Any(h => h.Public && h.Day == day);

    /// <summary>A day whose hours may not be the usual ones: a public holiday, or a day stores' hours are restricted.</summary>
    public static bool IsCautionDay(DateOnly day) => For(day.Year).Any(h => h.Day == day);

    private static IReadOnlyList<Holiday> Compute(int year)
    {
        var easter = Easter(year);
        var juneTwentyFourth = new DateOnly(year, 6, 24);
        var julyFirst = new DateOnly(year, 7, 1);
        var mayTwentyFourth = new DateOnly(year, 5, 24);

        List<Holiday> holidays =
        [
            new(new DateOnly(year, 1, 1), "new-year", true),
            new(new DateOnly(year, 1, 2), "jan-2", false),
            new(easter.AddDays(-2), "good-friday", true),
            new(easter, "easter", false),
            new(easter.AddDays(1), "easter-monday", true),
            new(mayTwentyFourth.AddDays(-(((int)mayTwentyFourth.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7)), "patriots", true),
            new(juneTwentyFourth, "fete-nationale", true),
            new(julyFirst, "canada-day", true),
            new(Monday(year, 9, 1), "labour-day", true),
            new(Monday(year, 10, 2), "thanksgiving", true),
            new(new DateOnly(year, 12, 25), "christmas", true),
            new(new DateOnly(year, 12, 26), "boxing-day", false),
        ];

        if (juneTwentyFourth.DayOfWeek == DayOfWeek.Sunday)
        {
            holidays.Add(new(juneTwentyFourth.AddDays(1), "fete-nationale", true));
        }

        if (julyFirst.DayOfWeek == DayOfWeek.Sunday)
        {
            holidays.Add(new(julyFirst.AddDays(1), "canada-day", true));
        }

        return holidays.OrderBy(h => h.Day).ToList().AsReadOnly();
    }

    /// <summary>The <paramref name="nth"/> Monday of the month.</summary>
    private static DateOnly Monday(int year, int month, int nth)
    {
        var first = new DateOnly(year, month, 1);
        return first.AddDays(((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7 + 7 * (nth - 1));
    }

    /// <summary>Easter Sunday, by the anonymous Gregorian computus (Meeus/Jones/Butcher).</summary>
    internal static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
