using static Essence.Hours.Tests.OpeningHoursTests;

namespace Essence.Hours.Tests;

public class CanonicalRuleTests
{
    [Theory]
    [InlineData("Mo,Tu,We,Th,Fr 06:00-21:30; Sa,Su 06:00-20:30", "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("Sa-Su 06:00-20:30; Mo-Fr 06:00-21:30", "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("Mo-Su 06:00-22:00; Su 08:00-20:00", "Mo-Sa 06:00-22:00; Su 08:00-20:00")]
    [InlineData("Fr-Mo 10:00-18:00", "Mo 10:00-18:00; Fr-Su 10:00-18:00")]
    [InlineData("Mo 08:00-12:00; We 08:00-12:00", "Mo 08:00-12:00; We 08:00-12:00")]
    [InlineData("Mo-Fr 13:00-17:00,08:00-12:00", "Mo-Fr 08:00-12:00,13:00-17:00")]
    [InlineData("Fr-Sa 22:00-02:00", "Fr-Sa 22:00-02:00")]
    [InlineData("Mo-Fr 06:00-23:59", "Mo-Fr 06:00-24:00")]
    [InlineData("Mo-Su off", "Mo-Su off")]
    [InlineData("Mo-Fr 08:00-17:00; Sa-Su off", "Mo-Fr 08:00-17:00")]
    [InlineData("PH 09:00-17:00", "Mo-Su off; PH 09:00-17:00")]
    [InlineData("PH off; Mo-Su 06:00-22:00", "Mo-Su 06:00-22:00")]
    [InlineData("PH off; 24/7", "24/7")]
    [InlineData("Mo-Fr,PH 06:00-22:00; Sa-Su 08:00-20:00", "Mo-Fr 06:00-22:00; Sa-Su 08:00-20:00")]
    [InlineData("PH off; Mo-Su 06:00-22:00; PH 09:00-17:00", "Mo-Su 06:00-22:00; PH 09:00-17:00")]
    [InlineData("Mo-Su 00:00-24:00", "24/7")]
    [InlineData("24/7; PH off", "Mo-Su 00:00-24:00; PH off")]
    public void The_rule_is_written_one_way(string text, string rule) =>
        Assert.Equal(rule, OpeningHours.Parse(text).ToRule());

    [Theory]
    [InlineData("24/7")]
    [InlineData("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("Mo-Fr 06:00-21:30; Sa 06:00-20:30; Su 06:00-19:30")]
    [InlineData("Mo-Fr 05:30-24:00; Sa-Su 06:00-24:00")]
    [InlineData("Sa-Su 00:00-02:00,05:00-24:00")]
    [InlineData("Mo-Su,PH 06:00-23:00")]
    [InlineData("Mo-Su closed")]
    [InlineData("PH off")]
    [InlineData("Fr-Mo 18:00-03:00; We 23:00-01:00")]
    [InlineData("Mo-Su 06:00-05:59; PH 08:00-12:00,13:00-17:00")]
    [InlineData("Mo-Fr 10:00-20:30, Sa 09:00-18:00,Su 10:00-17:00")]
    public void The_rule_reads_back_to_the_same_schedule(string text)
    {
        var hours = OpeningHours.Parse(text, lenient: true);
        var rule = hours.ToRule();

        var again = OpeningHours.Parse(rule);

        Assert.Equal(hours, again);
        Assert.Equal(rule, again.ToRule());
    }

    [Fact]
    public void Costco_s_week_in_two_groups()
    {
        var hours = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30");

        Assert.Equal(
            [new DayGroup(DayOfWeek.Monday, DayOfWeek.Friday, [Range(6, 21.5)]), new DayGroup(DayOfWeek.Saturday, DayOfWeek.Sunday, [Range(6, 20.5)])],
            hours.Groups());
    }

    [Fact]
    public void Closed_days_are_groups_too()
    {
        var hours = OpeningHours.Parse("Mo-Fr 08:00-17:00; Su 10:00-16:00");

        Assert.Equal(
            [
                new DayGroup(DayOfWeek.Monday, DayOfWeek.Friday, [Range(8, 17)]),
                new DayGroup(DayOfWeek.Saturday, DayOfWeek.Saturday, []),
                new DayGroup(DayOfWeek.Sunday, DayOfWeek.Sunday, [Range(10, 16)]),
            ],
            hours.Groups());
        Assert.Equal("Sa off", hours.Groups()[1].ToString());
    }

    [Fact]
    public void Always_open_is_one_group()
    {
        Assert.Equal([new DayGroup(DayOfWeek.Monday, DayOfWeek.Sunday, [TimeRange.AllDay])], OpeningHours.AlwaysOpen.Groups());
        Assert.Equal("24/7", OpeningHours.AlwaysOpen.ToRule());
    }

    [Fact]
    public void Schedules_are_equal_however_they_were_written()
    {
        var costco = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30");
        var same = OpeningHours.Parse("Sa,Su 06:00-20:30; Mo-Th 06:00-21:30; Fr 06:00-12:00,12:00-21:30");

        Assert.Equal(costco, same);
        Assert.True(costco == same);
        Assert.Equal(costco.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(costco, OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:00"));
        Assert.NotEqual(costco, OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30; PH off"));
        Assert.False(costco == null);
    }

    [Fact]
    public void The_editor_s_week_merges_its_ranges_and_closes_the_days_left_out()
    {
        var hours = OpeningHours.FromDays(
            new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>
            {
                [DayOfWeek.Monday] = [Range(11, 14), Range(6, 12)],
                [DayOfWeek.Friday] = [Range(22, 26)],
            },
            holidays: []);

        Assert.Equal([Range(6, 14)], hours.On(DayOfWeek.Monday));
        Assert.Empty(hours.On(DayOfWeek.Tuesday));
        Assert.Equal("Mo 06:00-14:00; Fr 22:00-02:00; PH off", hours.ToRule());
        Assert.Equal(hours, OpeningHours.Parse(hours.ToRule()));
    }

    [Fact]
    public void The_editor_s_week_open_all_day_is_24_7()
    {
        var hours = OpeningHours.FromDays(Enum.GetValues<DayOfWeek>().ToDictionary(d => d, IReadOnlyList<TimeRange> (_) => [TimeRange.AllDay]));

        Assert.True(hours.IsAlwaysOpen);
        Assert.Equal(OpeningHours.AlwaysOpen, hours);
    }

    [Fact]
    public void The_editor_s_end_at_23_59_is_midnight_as_when_parsed()
    {
        // A time picker that stops at 23:59.
        var hours = OpeningHours.FromDays(
            new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>> { [DayOfWeek.Monday] = [new TimeRange(600, 1439)] },
            holidays: [new TimeRange(0, 1439)]);

        Assert.Equal([Range(10, 24)], hours.On(DayOfWeek.Monday));
        Assert.Equal([TimeRange.AllDay], hours.OnHolidays);
        Assert.Equal("Mo 10:00-24:00; PH 00:00-24:00", hours.ToRule());
        Assert.Equal(hours, OpeningHours.Parse(hours.ToRule()));
        Assert.Equal(OpeningHours.Parse("Mo 10:00-23:59; PH 00:00-23:59"), hours);
    }

    [Fact]
    public void The_editor_s_week_open_until_23_59_every_day_is_24_7()
    {
        var hours = OpeningHours.FromDays(Enum.GetValues<DayOfWeek>().ToDictionary(d => d, IReadOnlyList<TimeRange> (_) => [new TimeRange(0, 1439)]));

        Assert.True(hours.IsAlwaysOpen);
        Assert.Equal(OpeningHours.AlwaysOpen, hours);
        Assert.Equal("24/7", hours.ToRule());
    }

    [Theory]
    [InlineData(600, 600)]
    [InlineData(600, 540)]
    [InlineData(-60, 600)]
    [InlineData(1440, 1500)]
    [InlineData(1320, 2760)]
    [InlineData(0, 1500)]
    public void The_editor_s_ranges_must_be_ranges_of_a_day(int start, int end)
    {
        var days = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>> { [DayOfWeek.Monday] = [new TimeRange(start, end)] };

        Assert.False(new TimeRange(start, end).IsValid);
        Assert.Throws<ArgumentException>(() => OpeningHours.FromDays(days));
    }

    [Fact]
    public void The_editor_s_day_can_t_run_into_more_than_a_whole_day()
    {
        var days = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>> { [DayOfWeek.Monday] = [TimeRange.AllDay, Range(22, 26)] };

        Assert.Throws<ArgumentException>(() => OpeningHours.FromDays(days));
    }

    [Theory]
    [InlineData(360, 1290, "06:00-21:30")]
    [InlineData(1080, 1440, "18:00-24:00")]
    [InlineData(1320, 1560, "22:00-02:00")]
    [InlineData(0, 1440, "00:00-24:00")]
    public void A_range_is_written_as_its_clock_times(int start, int end, string text) =>
        Assert.Equal(text, new TimeRange(start, end).ToString());
}
