namespace Essence.Hours.Tests;

public class OpeningHoursTests
{
    private static readonly DayOfWeek[] Week = Enum.GetValues<DayOfWeek>();

    [Fact]
    public void Open_all_the_time()
    {
        var hours = OpeningHours.Parse("24/7");

        Assert.True(hours.IsAlwaysOpen);
        Assert.All(Week, day => Assert.Equal([TimeRange.AllDay], hours.On(day)));
        Assert.Null(hours.OnHolidays);
        Assert.Equal(OpeningHours.AlwaysOpen, hours);
    }

    [Fact]
    public void Costco_s_weekdays_and_weekends()
    {
        var hours = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30");

        Assert.Equal([Range(6, 21.5)], hours.On(DayOfWeek.Monday));
        Assert.Equal([Range(6, 21.5)], hours.On(DayOfWeek.Friday));
        Assert.Equal([Range(6, 20.5)], hours.On(DayOfWeek.Saturday));
        Assert.Equal([Range(6, 20.5)], hours.On(DayOfWeek.Sunday));
        Assert.False(hours.IsAlwaysOpen);
        Assert.Null(hours.OnHolidays);
    }

    [Theory]
    [InlineData("24/7", "24/7")]
    [InlineData("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("Mo-Fr 06:00-21:30; Sa 06:00-20:30; Su 06:00-19:30", "Mo-Fr 06:00-21:30; Sa 06:00-20:30; Su 06:00-19:30")]
    [InlineData("Mo-Su 06:00-23:00", "Mo-Su 06:00-23:00")]
    [InlineData("Mo-Fr 05:30-24:00; Sa-Su 06:00-24:00", "Mo-Fr 05:30-24:00; Sa-Su 06:00-24:00")]
    [InlineData("Sa-Su 00:00-02:00,05:00-24:00", "Sa-Su 00:00-02:00,05:00-24:00")]
    [InlineData("Mo-Su 00:00-23:59", "24/7")]
    [InlineData("Mo-Su 00:00-24:00", "24/7")]
    [InlineData("Mo-Su,PH 06:00-23:00", "Mo-Su 06:00-23:00; PH 06:00-23:00")]
    [InlineData("Mo-Su closed", "Mo-Su off")]
    [InlineData("PH off", "Mo-Su off; PH off")]
    public void The_sources_values_are_read_strictly(string text, string rule) =>
        Assert.Equal(rule, OpeningHours.Parse(text).ToRule());

    [Fact]
    public void Some_sources_mean_all_day_by_23_59()
    {
        var hours = OpeningHours.Parse("Mo-Fr 06:00-23:59");

        Assert.Equal([Range(6, 24)], hours.On(DayOfWeek.Wednesday));
    }

    [Fact]
    public void Two_ranges_in_a_day()
    {
        var hours = OpeningHours.Parse("Sa-Su 00:00-02:00,05:00-24:00");

        Assert.Equal([Range(0, 2), Range(5, 24)], hours.On(DayOfWeek.Sunday));
        Assert.Empty(hours.On(DayOfWeek.Monday));
    }

    [Fact]
    public void Public_holidays_with_the_days()
    {
        var hours = OpeningHours.Parse("Mo-Su,PH 06:00-23:00");

        Assert.Equal([Range(6, 23)], hours.On(DayOfWeek.Tuesday));
        Assert.Equal([Range(6, 23)], hours.OnHolidays);
    }

    [Fact]
    public void Closed_on_public_holidays()
    {
        var hours = OpeningHours.Parse("Mo-Fr 08:00-17:00; PH off");

        Assert.Equal([Range(8, 17)], hours.On(DayOfWeek.Monday));
        Assert.NotNull(hours.OnHolidays);
        Assert.Empty(hours.OnHolidays);
    }

    [Fact]
    public void Closed_every_day()
    {
        var hours = OpeningHours.Parse("Mo-Su closed");

        Assert.All(Week, day => Assert.Empty(hours.On(day)));
        Assert.Null(hours.OnHolidays);
        Assert.False(hours.IsAlwaysOpen);
    }

    [Fact]
    public void A_range_that_ends_before_it_starts_spills_into_the_next_day()
    {
        var hours = OpeningHours.Parse("Fr-Sa 22:00-02:00");

        Assert.Equal([new TimeRange(1320, 1560)], hours.On(DayOfWeek.Friday));
        Assert.Equal([Range(22, 26)], hours.On(DayOfWeek.Saturday));

        // Saturday night's spill stays on Saturday.
        Assert.Empty(hours.On(DayOfWeek.Sunday));
        Assert.True(hours.On(DayOfWeek.Friday)[0].Spills);
    }

    [Fact]
    public void An_end_at_00_00_is_midnight_not_a_spill()
    {
        var hours = OpeningHours.Parse("Mo-Fr 18:00-00:00");

        Assert.Equal([Range(18, 24)], hours.On(DayOfWeek.Monday));
        Assert.Equal("Mo-Fr 18:00-24:00", hours.ToRule());
    }

    [Fact]
    public void A_later_rule_replaces_the_hours_of_the_days_it_names()
    {
        var hours = OpeningHours.Parse("Mo-Su 06:00-22:00; Su 08:00-20:00");

        Assert.Equal([Range(6, 22)], hours.On(DayOfWeek.Saturday));
        Assert.Equal([Range(8, 20)], hours.On(DayOfWeek.Sunday));
    }

    [Fact]
    public void Days_after_24_7_replace_its_hours()
    {
        var hours = OpeningHours.Parse("24/7; Su off");

        Assert.Equal([TimeRange.AllDay], hours.On(DayOfWeek.Saturday));
        Assert.Empty(hours.On(DayOfWeek.Sunday));
        Assert.False(hours.IsAlwaysOpen);
    }

    [Fact]
    public void A_rule_without_days_is_every_day()
    {
        var hours = OpeningHours.Parse("06:00-22:00; PH off");

        Assert.All(Week, day => Assert.Equal([Range(6, 22)], hours.On(day)));
        Assert.Empty(hours.OnHolidays!);
    }

    [Fact]
    public void Days_a_rule_doesn_t_name_are_closed()
    {
        var hours = OpeningHours.Parse("Mo,We,Fr 08:00-12:00");

        Assert.Equal([Range(8, 12)], hours.On(DayOfWeek.Wednesday));
        Assert.Empty(hours.On(DayOfWeek.Tuesday));
        Assert.Empty(hours.On(DayOfWeek.Sunday));
    }

    [Fact]
    public void A_range_of_days_can_wrap_around_the_weekend()
    {
        var hours = OpeningHours.Parse("Fr-Mo 10:00-18:00");

        Assert.All([DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday], day => Assert.Equal([Range(10, 18)], hours.On(day)));
        Assert.All([DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], day => Assert.Empty(hours.On(day)));
    }

    [Theory]
    [InlineData("Mo 12:00-18:00,08:00-13:00")]
    [InlineData("Mo 08:00-12:00,12:00-18:00")]
    [InlineData("Mo 08:00-18:00,09:00-10:00")]
    public void Ranges_that_overlap_or_meet_are_one(string text) =>
        Assert.Equal([Range(8, 18)], OpeningHours.Parse(text).On(DayOfWeek.Monday));

    [Fact]
    public void Open_all_day_on_holidays_too_is_24_7()
    {
        var hours = OpeningHours.Parse("Mo-Su,PH 00:00-24:00");

        Assert.True(hours.IsAlwaysOpen);
        Assert.Null(hours.OnHolidays);
        Assert.Equal(OpeningHours.AlwaysOpen, hours);
    }

    [Fact]
    public void Closed_on_holidays_isn_t_always_open()
    {
        var hours = OpeningHours.Parse("24/7; PH off");

        Assert.False(hours.IsAlwaysOpen);
        Assert.Equal("Mo-Su 00:00-24:00; PH off", hours.ToRule());
    }

    [Fact]
    public void A_later_rule_replaces_PH_s_hours_on_the_holidays_that_fall_then()
    {
        // As in OpenStreetMap: a holiday Monday is a Monday, so "Mo-Su" names it too.
        var hours = OpeningHours.Parse("PH off; Mo-Su 06:00-22:00");

        Assert.All(Week, day => Assert.Equal([Range(6, 22)], hours.On(day)));
        Assert.Null(hours.OnHolidays);
        Assert.Equal(OpeningHours.AlwaysOpen, OpeningHours.Parse("PH off; 24/7"));
        Assert.Null(OpeningHours.Parse("PH 09:00-17:00; 06:00-22:00").OnHolidays);
    }

    [Fact]
    public void A_later_rule_for_some_days_leaves_PH_s_hours_where_they_are_the_days_anyway()
    {
        // A Saturday holiday has Saturday's hours; a weekday holiday has PH's, which are the weekdays'.
        var hours = OpeningHours.Parse("Mo-Fr,PH 06:00-22:00; Sa-Su 08:00-20:00");

        Assert.Equal([Range(8, 20)], hours.On(DayOfWeek.Saturday));
        Assert.Null(hours.OnHolidays);

        // And closed on the days left alone, as PH: a holiday then is closed either way.
        Assert.Null(OpeningHours.Parse("PH off; Mo-Fr 06:00-22:00").OnHolidays);
    }

    [Fact]
    public void PH_named_again_after_the_days_has_its_own_hours()
    {
        var hours = OpeningHours.Parse("PH off; Mo-Su 06:00-22:00; PH 09:00-17:00");

        Assert.Equal([Range(9, 17)], hours.OnHolidays);
    }

    [Theory]
    [InlineData("PH 09:00-17:00; Mo-Fr 06:00-22:00", "PH's hours come before those of Mo-Fr")]
    [InlineData("Mo-Su 06:00-22:00; PH off; Sa-Su 08:00-20:00", "PH's hours come before those of Sa-Su")]
    [InlineData("Mo-Su 10:00-14:00; PH off; Mo,We-Th 06:00-22:00; Sa-Su 08:00-12:00", "PH's hours come before those of Mo,We-Th,Sa-Su")]
    public void PH_s_hours_that_would_hold_on_only_some_holidays_are_refused(string text, string reason)
    {
        foreach (var lenient in (bool[])[false, true])
        {
            Assert.False(OpeningHours.TryParse(text, lenient, out var hours, out var error));
            Assert.Null(hours);
            Assert.Contains(reason, error);
            Assert.Contains("put PH last", error);
        }
    }

    [Fact]
    public void Days_after_PH_s_hours_and_a_comma_are_refused_even_when_lenient()
    {
        // OpenStreetMap's "," would add Monday's hours to a holiday Monday's; a ";" would replace them.
        Assert.False(OpeningHours.TryParse("Sa-Su,PH 09:00-17:00, Mo-Fr 18:00-22:00", true, out _, out var error));

        Assert.Contains("A rule for Mo-Fr comes after PH's hours and a \",\"", error);

        // After "PH off" the two mean the same.
        Assert.Null(OpeningHours.Parse("PH off, Mo-Fr 06:00-22:00", lenient: true).OnHolidays);
    }

    [Theory]
    [InlineData("Mo-Fr 12:00-13:00 off")]
    [InlineData("Mo-Fr 08:00-12:00,13:00-17:00 closed")]
    public void Off_after_the_hours_is_refused_for_what_it_is(string text)
    {
        foreach (var lenient in (bool[])[false, true])
        {
            Assert.False(OpeningHours.TryParse(text, lenient, out _, out var error));
            Assert.Contains("takes the place of a rule's hours", error);
            Assert.DoesNotContain("lowercase", error);
        }

        Assert.False(OpeningHours.TryParse("Mo-Fr off OFF", true, out _, out var twice));
        Assert.Contains("takes the place of a rule's hours", twice);
    }

    [Fact]
    public void Strict_says_off_is_written_in_lowercase()
    {
        Assert.False(OpeningHours.TryParse("Mo-Fr Off", false, out _, out var error));

        Assert.Equal("\"Off\" is written in lowercase.", error);
    }

    [Theory]
    [InlineData("Mo-Fr 10:00-20:30, Sa 09:00-18:00,Su 10:00-17:00", "Mo-Fr 10:00-20:30; Sa 09:00-18:00; Su 10:00-17:00")]
    [InlineData("Mo-Su 06:00-22:00, PH off", "Mo-Su 06:00-22:00; PH off")]
    [InlineData("mo-fr 06:00-21:30; SA-SU 06:00-20:30", "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("Mo-Fr 6:00-21:30; Sa-Su 6:00-9:30", "Mo-Fr 06:00-21:30; Sa-Su 06:00-09:30")]
    [InlineData("Mo–Fr 06:00–21:30; Sa—Su 06:00—20:30", "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("  Mo - Fr   06:00 - 21:30 ;Sa-Su06:00-20:30\t", "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30")]
    [InlineData("Mo-Fr 06:00-21:30, 22:00-23:00", "Mo-Fr 06:00-21:30,22:00-23:00")]
    [InlineData("Mo-Fr 06:00-21:30;", "Mo-Fr 06:00-21:30")]
    [InlineData("Mo-Su OFF; Ph Closed", "Mo-Su off; PH off")]
    public void Lenient_reads_what_sources_get_wrong_and_strict_refuses_it(string text, string rule)
    {
        Assert.Equal(rule, OpeningHours.Parse(text, lenient: true).ToRule());
        Assert.False(OpeningHours.TryParse(text, lenient: false, out _, out _));
    }

    [Fact]
    public void Strict_says_rules_are_separated_by_a_semicolon()
    {
        Assert.False(OpeningHours.TryParse("Mo-Fr 10:00-20:30, Sa 09:00-18:00", false, out _, out var comma));
        Assert.False(OpeningHours.TryParse("Mo-Fr 10:00-20:30;Sa 09:00-18:00", false, out _, out var space));

        Assert.Contains("\"; \"", comma);
        Assert.Contains("\"; \"", space);
    }

    [Fact]
    public void Strict_says_how_to_write_a_one_digit_hour()
    {
        Assert.False(OpeningHours.TryParse("Mo-Fr 6:00-21:30", false, out _, out var error));

        Assert.Contains("06:00", error);
    }

    [Fact]
    public void Strict_says_how_days_are_written()
    {
        Assert.False(OpeningHours.TryParse("mo-fr 06:00-21:30", false, out _, out var error));

        Assert.Contains("Mo, Tu, We, Th, Fr, Sa, Su", error);
    }

    [Fact]
    public void A_comma_before_a_day_named_again_is_refused_even_when_lenient()
    {
        // OpenStreetMap's "," would add Sunday's hours to the first rule's; a ";" would replace them.
        Assert.False(OpeningHours.TryParse("Mo-Su 06:00-22:00, Su 08:00-20:00", true, out var hours, out var error));

        Assert.Null(hours);
        Assert.Contains("Su is named again", error);
    }

    [Theory]
    [InlineData("Mo-Su,PH 08:00+", "Open ends (\"08:00+\")")]
    [InlineData("Mo-Fr 08:00-17:00+", "Open ends")]
    [InlineData("sunrise-sunset", "Sunrise")]
    [InlineData("Mo-Fr 06:00-sunset", "Sunrise")]
    [InlineData("Mo-Fr 08:00-17:00 \"call ahead\"", "Comments")]
    [InlineData("\"by appointment\"", "Comments")]
    [InlineData("Dec 25 off", "Months")]
    [InlineData("Mo-Su 06:00-22:00; Dec 24 06:00-17:00", "Months")]
    [InlineData("2026 Mo-Fr 08:00-17:00", "2026")]
    [InlineData("week 01-26 Mo-Fr 08:00-17:00", "Weeks")]
    [InlineData("easter off", "easter")]
    [InlineData("SH off", "School holidays")]
    [InlineData("Mo-Fr 08:00-17:00 || Sa 10:00-14:00", "Fallback")]
    [InlineData("Mo-Fr 08:00-17:00 open", "\"open\"")]
    [InlineData("Mo-Fr 08:00-17:00 unknown", "\"unknown\"")]
    [InlineData("Mo-Fr 10:00-16:00/90", "Repeating")]
    [InlineData("Mo-Fr 08:00-25:00", "25:00")]
    [InlineData("Mo-Fr 08:00-24:30", "24:30")]
    [InlineData("Mo-Fr 24:00-02:00", "can only end")]
    [InlineData("Mo-Fr 06:60-21:00", "minutes go up to 59")]
    [InlineData("Mo-Fr 06:5-21:00", "HH:MM")]
    [InlineData("Mo-Fr 0600-2100", "0600")]
    [InlineData("Mo-Su 00:00-00:00", "starts and ends at the same time")]
    [InlineData("Mo-Su 12:00-12:00", "starts and ends at the same time")]
    [InlineData("Mo 00:00-24:00,22:00-02:00", "more than a whole day")]
    [InlineData("Mo-Fr", "\"Mo-Fr\" has no hours")]
    [InlineData("Mo-Fr 08:00", "\"08:00\" has no end")]
    [InlineData("Mo-Fr 08:00-", "end too soon")]
    [InlineData("Mo-PH 08:00-12:00", "A range of days ends with a day")]
    [InlineData("Mo[1] 08:00-12:00", "\"[\"")]
    [InlineData("Lu-Ve 08:00-12:00", "\"Lu\"")]
    [InlineData("call us", "\"call\"")]
    [InlineData("?", "\"?\"")]
    [InlineData("Mo-Fr 08:00-12:00 Sa 09:00-12:00", "one is missing before \"Sa\"")]
    [InlineData("08:00-Mo 12:00", "\"Mo\"")]
    public void What_can_t_be_told_for_sure_is_refused_in_both_modes(string text, string reason)
    {
        foreach (var lenient in (bool[])[false, true])
        {
            Assert.False(OpeningHours.TryParse(text, lenient, out var hours, out var error));
            Assert.Null(hours);
            Assert.Contains(reason, error);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_text_has_no_hours(string? text)
    {
        Assert.False(OpeningHours.TryParse(text, lenient: true, out _, out var error));

        Assert.Equal("There are no hours.", error);
    }

    [Fact]
    public void Parse_throws_the_reason()
    {
        var thrown = Assert.Throws<FormatException>(() => OpeningHours.Parse("Mo-Su,PH 08:00+", lenient: true));

        Assert.Contains("Open ends", thrown.Message);
    }

    [Fact]
    public void Strict_refuses_stray_spaces()
    {
        foreach (var text in (string[])[" 24/7", "24/7 ", "Mo-Fr  06:00-21:30", "Mo-Fr 06:00-21:30;  Sa 06:00-20:30", "Mo-Su, PH 06:00-23:00"])
        {
            Assert.False(OpeningHours.TryParse(text, false, out _, out _), text);
            Assert.True(OpeningHours.TryParse(text, true, out _, out _), text);
        }
    }

    /// <summary>A range from hours as numbers: <c>Range(6, 21.5)</c> is 06:00-21:30, <c>Range(22, 26)</c> 22:00-02:00.</summary>
    internal static TimeRange Range(double fromHour, double toHour) => new((int)(fromHour * 60), (int)(toHour * 60));
}
