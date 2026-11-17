namespace Essence.Hours.Tests;

public class QuebecHolidaysTests
{
    [Fact]
    public void The_holidays_of_2026()
    {
        Assert.Equal(
            [
                new Holiday(new(2026, 1, 1), "new-year", true),
                new Holiday(new(2026, 1, 2), "jan-2", false),
                new Holiday(new(2026, 4, 3), "good-friday", true),
                new Holiday(new(2026, 4, 5), "easter", false),
                new Holiday(new(2026, 4, 6), "easter-monday", true),
                new Holiday(new(2026, 5, 18), "patriots", true),
                new Holiday(new(2026, 6, 24), "fete-nationale", true),
                new Holiday(new(2026, 7, 1), "canada-day", true),
                new Holiday(new(2026, 9, 7), "labour-day", true),
                new Holiday(new(2026, 10, 12), "thanksgiving", true),
                new Holiday(new(2026, 12, 25), "christmas", true),
                new Holiday(new(2026, 12, 26), "boxing-day", false),
            ],
            QuebecHolidays.For(2026));
    }

    [Fact]
    public void In_2029_june_24_and_july_1_are_sundays_so_the_mondays_are_off_too()
    {
        var holidays = QuebecHolidays.For(2029);

        Assert.Equal(
            [new DateOnly(2029, 6, 24), new DateOnly(2029, 6, 25)],
            holidays.Where(h => h.Key == "fete-nationale").Select(h => h.Day));
        Assert.Equal(
            [new DateOnly(2029, 7, 1), new DateOnly(2029, 7, 2)],
            holidays.Where(h => h.Key == "canada-day").Select(h => h.Day));
        Assert.True(QuebecHolidays.IsPublicHoliday(new DateOnly(2029, 6, 25)));
        Assert.True(QuebecHolidays.IsPublicHoliday(new DateOnly(2029, 7, 2)));
        Assert.False(QuebecHolidays.IsPublicHoliday(new DateOnly(2026, 6, 25)));
    }

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void Easter_by_the_computus(int year, int month, int day)
    {
        var easter = new DateOnly(year, month, day);
        var holidays = QuebecHolidays.For(year);

        Assert.Equal(easter, holidays.Single(h => h.Key == "easter").Day);
        Assert.Equal(easter.AddDays(-2), holidays.Single(h => h.Key == "good-friday").Day);
        Assert.Equal(easter.AddDays(1), holidays.Single(h => h.Key == "easter-monday").Day);
    }

    [Theory]
    [InlineData(2025, 19)] // May 24 is a Saturday
    [InlineData(2026, 18)] // a Sunday
    [InlineData(2027, 24)] // a Monday: the Monday before the 25th is the 24th
    [InlineData(2028, 22)] // a Wednesday
    public void The_patriots_day_is_the_monday_before_may_25(int year, int day) =>
        Assert.Equal(new DateOnly(year, 5, day), QuebecHolidays.For(year).Single(h => h.Key == "patriots").Day);

    [Fact]
    public void Public_holidays_are_caution_days_and_restricted_days_only_caution_days()
    {
        Assert.True(QuebecHolidays.IsPublicHoliday(new DateOnly(2026, 12, 25)));
        Assert.True(QuebecHolidays.IsCautionDay(new DateOnly(2026, 12, 25)));

        foreach (var day in (DateOnly[])[new(2026, 12, 26), new(2027, 1, 2), new(2026, 4, 5)])
        {
            Assert.False(QuebecHolidays.IsPublicHoliday(day));
            Assert.True(QuebecHolidays.IsCautionDay(day));
        }
    }

    [Fact]
    public void Remembrance_day_isn_t_one_in_quebec()
    {
        Assert.False(QuebecHolidays.IsPublicHoliday(new DateOnly(2026, 11, 11)));
        Assert.False(QuebecHolidays.IsCautionDay(new DateOnly(2026, 11, 11)));
    }
}
