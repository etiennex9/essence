using Essence.Hours;
using static Essence.Ranking.Tests.TrendData;

namespace Essence.Ranking.Tests;

public class PricePatternTests
{
    private static readonly DateOnly Today = new(2026, 12, 20);

    /// <summary>
    /// <paramref name="days"/> days up to today of an area of 20 stations at 150.0: the first 14 go up 10 ¢ at about
    /// <paramref name="riseHour"/> (5, 10 or 15 minutes past) and come down 2 ¢ at 06:00, 09:00, 12:00, 15:00 and 18:00; the
    /// others don't move. <paramref name="extra"/> adds to every station's price on a day (in cents).
    /// </summary>
    private static List<AreaStation> Area(
        int days, TimeZoneInfo zone, Func<DateOnly, int>? riseHour = null, Func<DateOnly, decimal>? extra = null, int stations = 20)
    {
        var area = new List<AreaStation>();
        for (var i = 0; i < stations; i++)
        {
            var changes = new List<(DateTimeOffset At, decimal? Cents)>();
            for (var day = Today.AddDays(-days); day <= Today; day = day.AddDays(1))
            {
                var shift = extra?.Invoke(day) ?? 0;
                changes.Add((At(day, 0, 0, zone), 150.0m + shift));
                if (i < 14 && riseHour is not null)
                {
                    var hour = riseHour(day);
                    changes.Add((At(day, hour, 5 + i % 3 * 5, zone), 160.0m + shift));
                    for (var (down, step) = (hour < 6 ? 6 : hour + 3, 1); down < 24 && step <= 5; down += 3, step++)
                    {
                        changes.Add((At(day, down, 0, zone), 160.0m - 2 * step + shift));
                    }
                }
            }

            area.Add(Station(i, [.. changes.DistinctBy(c => c.At).OrderBy(c => c.At)]));
        }

        return area;
    }

    private static Coverage Covered(int days, TimeZoneInfo zone) => Imports(At(Today.AddDays(-days), 0, 0, zone), At(Today, 10, 0, zone));

    private static AreaPattern? Find(List<AreaStation> area, int days, TimeZoneInfo? zone = null) =>
        PricePattern.Find(area, Covered(days, zone ?? Zone), At(Today, 10, 0, zone ?? Zone).UtcTicks, zone ?? Zone);

    [Fact]
    public void Jumps_around_midnight_and_lowest_in_the_evening()
    {
        var pattern = Find(Area(30, Zone, _ => 0), 30)!;

        Assert.Equal(new TimeOnly(0, 0), pattern.JumpsAround);
        Assert.Equal((18, 0), (pattern.LowestFromHour, pattern.LowestToHour));
        Assert.Equal(28, pattern.Days);
        Assert.Equal(Enumerable.Range(0, 14).Select(i => (long)i), pattern.Followers.Ids);
    }

    [Fact]
    public void Flat_prices_have_no_pattern()
    {
        var pattern = Find(Area(30, Zone), 30)!;

        Assert.Null(pattern.JumpsAround);
        Assert.Null(pattern.LowestFromHour);
        Assert.Empty(pattern.Followers.Ids);
    }

    [Fact]
    public void Jumps_at_any_hour_have_no_time()
    {
        var pattern = Find(Area(30, Zone, day => day.DayNumber * 7 % 22), 30)!;

        Assert.Null(pattern.JumpsAround);
    }

    [Fact]
    public void Nine_days_of_data_have_no_pattern_and_seven_jumps_no_followers()
    {
        var nine = Find(Area(9, Zone, _ => 0), 9)!;
        var seven = Find(Area(7, Zone, _ => 0), 7)!;

        Assert.Null(nine.JumpsAround);
        Assert.Null(nine.LowestFromHour);
        Assert.Equal(14, nine.Followers.Count);
        Assert.Empty(seven.Followers.Ids);
    }

    [Fact]
    public void A_small_area_has_no_pattern()
    {
        Assert.Null(Find(Area(30, Zone, _ => 0, stations: 7), 30));
    }

    [Fact]
    public void The_islands_are_on_their_own_clock()
    {
        var pattern = Find(Area(30, QuebecTime.Atlantic, _ => 0), 30, QuebecTime.Atlantic)!;

        Assert.Equal(new TimeOnly(0, 0), pattern.JumpsAround);
    }

    [Fact]
    public void Cheaper_on_thursdays_and_fridays()
    {
        var pattern = Find(Area(60, Zone, extra: day => day.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Friday ? -3 : 0), 60)!;

        Assert.Equal([4, 5], pattern.CheapestDays);
        Assert.True(pattern.Weeks >= TrendRules.WeeklyMinWeeks);
    }

    [Fact]
    public void The_window_kept_tells_the_days_even_with_a_day_missing()
    {
        var days = (int)TrendRules.Window.TotalDays;
        var outage = Today.AddDays(-20);
        var coverage = Imports(
            At(Today.AddDays(-days), 0, 0, Zone), At(Today, 10, 0, Zone), (At(outage, 8, 0, Zone), At(outage, 14, 0, Zone)));
        var area = Area(days, Zone, extra: day => day.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Friday ? -3 : 0);

        var pattern = PricePattern.Find(area, coverage, At(Today, 10, 0, Zone).UtcTicks, Zone)!;

        Assert.Equal([4, 5], pattern.CheapestDays);
    }

    [Fact]
    public void Five_weeks_or_an_irregular_week_say_no_day()
    {
        static decimal Cheaper(DateOnly day) => day.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Friday ? -3 : 0;
        static decimal Alternating(DateOnly day) => day.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Friday ? (day.DayNumber / 7 % 2 == 0 ? -3 : 3) : 0;

        Assert.Null(Find(Area(38, Zone, extra: Cheaper), 38)!.CheapestDays);
        Assert.Null(Find(Area(60, Zone, extra: Alternating), 60)!.CheapestDays);
    }

    [Fact]
    public void Jumps_come_as_events_with_the_stations_that_joined()
    {
        var area = Area(30, Zone, _ => 0);
        var from = At(Today.AddDays(-28), 0, 0, Zone).UtcTicks;
        var to = At(Today, 0, 0, Zone).UtcTicks;

        var events = PricePattern.JumpEvents(area, Covered(30, Zone), from, to, At(Today, 10, 0, Zone).UtcTicks, out var followers);

        Assert.Equal(28, events.Count);
        Assert.All(events, e => Assert.Equal(new TimeOnly(0, 5), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(e, TimeSpan.Zero), Zone).DateTime)));
        Assert.Equal(14, followers.Count);
    }
}
