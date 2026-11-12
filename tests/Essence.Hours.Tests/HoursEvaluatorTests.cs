namespace Essence.Hours.Tests;

public class HoursEvaluatorTests
{
    private static readonly OpeningHours CostcoLaval = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30");
    private static readonly TimeZoneInfo Montreal = QuebecTime.Eastern;
    private static readonly TimeSpan HalfAnHour = TimeSpan.FromMinutes(30);

    [Fact]
    public void Open_on_a_wednesday_evening_until_21_30()
    {
        var at = Local(2026, 11, 11, 21, 15);

        var state = HoursEvaluator.At(CostcoLaval, Montreal, at);

        Assert.True(state.IsOpen);
        Assert.Equal(Utc(2026, 11, 12, 2, 30), state.ChangesAt);
        Assert.Equal(TimeSpan.FromHours(-5), state.ChangesAt!.Value.Offset);
        Assert.False(state.IsHoliday);
        Assert.False(state.HolidayCaution);
    }

    [Fact]
    public void Getting_there_a_quarter_hour_before_it_closes_is_closing_soon()
    {
        var check = HoursEvaluator.OnArrival(CostcoLaval, Montreal, Local(2026, 11, 11, 21, 15), HalfAnHour);

        Assert.Equal(Arrival.ClosesSoon, check.State);
        Assert.Equal(Utc(2026, 11, 12, 2, 30), check.ClosesAt);
        Assert.Equal(Utc(2026, 11, 12, 11, 0), check.OpensAt);
    }

    [Fact]
    public void Closed_after_21_30_until_thursday_morning()
    {
        var at = Local(2026, 11, 11, 21, 45);

        var state = HoursEvaluator.At(CostcoLaval, Montreal, at);
        var check = HoursEvaluator.OnArrival(CostcoLaval, Montreal, at, HalfAnHour);

        Assert.False(state.IsOpen);
        Assert.Equal(Local(2026, 11, 12, 6, 0), state.ChangesAt);
        Assert.Equal(Utc(2026, 11, 12, 11, 0), state.ChangesAt);
        Assert.Equal(new ArrivalCheck(Arrival.Closed, null, Utc(2026, 11, 12, 11, 0), false), check);
    }

    [Fact]
    public void Closing_soon_is_when_it_closes_within_the_time_given()
    {
        // It closes at 21:30.
        var early = HoursEvaluator.OnArrival(CostcoLaval, Montreal, Local(2026, 11, 11, 20, 30), HalfAnHour);
        var onTheDot = HoursEvaluator.OnArrival(CostcoLaval, Montreal, Local(2026, 11, 11, 21, 0), HalfAnHour);

        Assert.Equal(Arrival.Open, early.State);
        Assert.Equal(Utc(2026, 11, 12, 2, 30), early.ClosesAt);
        Assert.Equal(Arrival.ClosesSoon, onTheDot.State);
    }

    [Fact]
    public void Weekends_close_at_20_30()
    {
        var saturday = HoursEvaluator.OnArrival(CostcoLaval, Montreal, Local(2026, 11, 14, 20, 15), HalfAnHour);
        var sunday = HoursEvaluator.OnArrival(CostcoLaval, Montreal, Local(2026, 11, 15, 20, 35), HalfAnHour);

        Assert.Equal(Arrival.ClosesSoon, saturday.State);
        Assert.Equal(Local(2026, 11, 14, 20, 30), saturday.ClosesAt);
        Assert.Equal(Arrival.Closed, sunday.State);
        Assert.Equal(Local(2026, 11, 16, 6, 0), sunday.OpensAt);
    }

    [Fact]
    public void Open_24_7_never_changes()
    {
        var state = HoursEvaluator.At(OpeningHours.AlwaysOpen, Montreal, Local(2026, 11, 11, 3, 0));
        var check = HoursEvaluator.OnArrival(OpeningHours.AlwaysOpen, Montreal, Local(2026, 11, 11, 3, 0), HalfAnHour);

        Assert.Equal(new HoursState(true, null, false, false), state);
        Assert.Equal(new ArrivalCheck(Arrival.Open, null, null, false), check);
    }

    [Fact]
    public void Every_day_all_day_is_one_stretch_that_never_closes() =>
        Assert.Null(HoursEvaluator.At(OpeningHours.Parse("Mo-Su 00:00-24:00; PH 00:00-24:00"), Montreal, Local(2026, 11, 11, 23, 59)).ChangesAt);

    [Fact]
    public void Closed_every_day_never_opens()
    {
        var hours = OpeningHours.Parse("Mo-Su off");

        Assert.Equal(new HoursState(false, null, false, false), HoursEvaluator.At(hours, Montreal, Local(2026, 11, 11, 12, 0)));
        Assert.Equal(new ArrivalCheck(Arrival.Closed, null, null, false), HoursEvaluator.OnArrival(hours, Montreal, Local(2026, 11, 11, 12, 0), HalfAnHour));
    }

    [Fact]
    public void Open_past_midnight_on_friday_and_saturday_nights()
    {
        var hours = OpeningHours.Parse("Fr-Sa 22:00-02:00");

        var saturdayNight = HoursEvaluator.At(hours, Montreal, Local(2026, 11, 14, 1, 0));
        var sundayNight = HoursEvaluator.At(hours, Montreal, Local(2026, 11, 15, 1, 0));
        var sundayLater = HoursEvaluator.At(hours, Montreal, Local(2026, 11, 15, 3, 0));

        Assert.Equal(new HoursState(true, Local(2026, 11, 14, 2, 0), false, false), saturdayNight);
        Assert.Equal(new HoursState(true, Local(2026, 11, 15, 2, 0), false, false), sundayNight);
        Assert.Equal(new HoursState(false, Local(2026, 11, 20, 22, 0), false, false), sundayLater);
    }

    [Fact]
    public void Saturday_night_s_spill_is_kept_when_sunday_has_hours_of_its_own()
    {
        // Unlike OpenStreetMap, which closes it early on Sunday: its own wiki calls that probably not intended.
        var hours = OpeningHours.Parse("Sa 16:00-03:00; Su 16:00-23:00");

        Assert.Equal(new HoursState(true, Local(2026, 11, 15, 3, 0), false, false), HoursEvaluator.At(hours, Montreal, Local(2026, 11, 15, 1, 0)));
        Assert.Equal("Sa 16:00-03:00; Su 16:00-23:00", hours.ToRule());
    }

    [Fact]
    public void Sunday_s_hours_spill_into_monday() =>
        Assert.Equal(
            new HoursState(true, Local(2026, 11, 16, 2, 0), false, false),
            HoursEvaluator.At(OpeningHours.Parse("Su 22:00-02:00"), Montreal, Local(2026, 11, 16, 1, 0)));

    [Fact]
    public void Open_until_midnight_and_from_midnight_doesn_t_close_at_midnight() =>
        Assert.Equal(
            Local(2026, 11, 14, 3, 0),
            HoursEvaluator.At(OpeningHours.Parse("Fr 18:00-24:00; Sa 00:00-03:00"), Montreal, Local(2026, 11, 13, 23, 0)).ChangesAt);

    [Fact]
    public void A_station_open_once_a_week_still_has_its_next_opening() =>
        Assert.Equal(
            Local(2026, 11, 18, 10, 0),
            HoursEvaluator.At(OpeningHours.Parse("We 10:00-12:00"), Montreal, Local(2026, 11, 11, 12, 30)).ChangesAt);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Springing_forward_the_night_stays_open_until_3_00(bool fallback)
    {
        var zone = Zone(fallback);
        var hours = OpeningHours.Parse("Mo-Su 00:00-03:00");

        // 2026-03-08: 02:00 EST becomes 03:00 EDT. At 01:30 EST (06:30Z) it's open until 03:00 EDT (07:00Z).
        var before = HoursEvaluator.At(hours, zone, Utc(2026, 3, 8, 6, 30));
        var after = HoursEvaluator.At(hours, zone, Utc(2026, 3, 8, 7, 30));

        Assert.Equal(new HoursState(true, Utc(2026, 3, 8, 7, 0), false, false), before);
        Assert.Equal(TimeSpan.FromHours(-4), before.ChangesAt!.Value.Offset);
        Assert.Equal(new HoursState(false, Utc(2026, 3, 9, 4, 0), false, false), after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_time_in_the_spring_forward_gap_moves_forward_by_the_gap(bool fallback)
    {
        var zone = Zone(fallback);

        // 02:30 doesn't happen on 2026-03-08: opening then is opening at 03:30 EDT (07:30Z), and closing then too.
        var opens = HoursEvaluator.At(OpeningHours.Parse("Mo-Su 02:30-22:00"), zone, Utc(2026, 3, 8, 6, 0));
        var closes = HoursEvaluator.At(OpeningHours.Parse("Mo-Su 18:00-02:30"), zone, Utc(2026, 3, 8, 6, 45));

        Assert.Equal(new HoursState(false, Utc(2026, 3, 8, 7, 30), false, false), opens);
        Assert.Equal(new HoursState(true, Utc(2026, 3, 8, 7, 30), false, false), closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Falling_back_the_night_is_open_through_both_1_30s(bool fallback)
    {
        var zone = Zone(fallback);
        var hours = OpeningHours.Parse("Mo-Su 00:00-03:00");

        // 2026-11-01: 02:00 EDT becomes 01:00 EST, so 01:30 comes twice (05:30Z and 06:30Z), and 03:00 EST is 08:00Z.
        var first = HoursEvaluator.At(hours, zone, Utc(2026, 11, 1, 5, 30));
        var second = HoursEvaluator.At(hours, zone, Utc(2026, 11, 1, 6, 30));

        Assert.Equal(new HoursState(true, Utc(2026, 11, 1, 8, 0), false, false), first);
        Assert.Equal(new HoursState(true, Utc(2026, 11, 1, 8, 0), false, false), second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_time_the_fall_back_repeats_is_the_first_one(bool fallback)
    {
        var zone = Zone(fallback);

        // Opening at 01:30 is at the first, in daylight time (05:30Z); closing at 01:30 too.
        var opens = HoursEvaluator.At(OpeningHours.Parse("Mo-Su 01:30-23:00"), zone, Utc(2026, 11, 1, 5, 0));
        var closed = HoursEvaluator.At(OpeningHours.Parse("Mo-Su 06:00-01:30"), zone, Utc(2026, 11, 1, 6, 15));

        Assert.Equal(new HoursState(false, Utc(2026, 11, 1, 5, 30), false, false), opens);
        Assert.Equal(new HoursState(false, Utc(2026, 11, 1, 11, 0), false, false), closed);
    }

    [Fact]
    public void A_station_on_the_islands_keeps_atlantic_time()
    {
        // 21:15 in Montréal is 22:15 in Cap-aux-Meules, where Costco's hours would have it closed.
        var at = Local(2026, 11, 11, 21, 15);

        Assert.True(HoursEvaluator.At(CostcoLaval, Montreal, at).IsOpen);
        Assert.False(HoursEvaluator.At(CostcoLaval, QuebecTime.ZoneFor(47.38, -61.86), at).IsOpen);
    }

    [Fact]
    public void Christmas_without_holiday_hours_is_a_caution()
    {
        var state = HoursEvaluator.At(CostcoLaval, Montreal, Local(2026, 12, 25, 12, 0));

        // A Friday: its own hours, which may not hold.
        Assert.Equal(new HoursState(true, Local(2026, 12, 25, 21, 30), true, true), state);
        Assert.True(HoursEvaluator.OnArrival(CostcoLaval, Montreal, Local(2026, 12, 25, 12, 0), HalfAnHour).HolidayCaution);
    }

    [Fact]
    public void Open_24_7_is_no_caution_on_a_holiday() =>
        Assert.Equal(new HoursState(true, null, true, false), HoursEvaluator.At(OpeningHours.AlwaysOpen, Montreal, Local(2026, 12, 25, 12, 0)));

    [Fact]
    public void A_rule_that_states_its_holidays_is_no_caution_on_one()
    {
        var hours = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30; PH off");

        Assert.Equal(new HoursState(false, Local(2026, 12, 26, 6, 0), true, false), HoursEvaluator.At(hours, Montreal, Local(2026, 12, 25, 12, 0)));
    }

    [Fact]
    public void Boxing_day_is_a_caution_even_when_the_rule_states_its_holidays()
    {
        var hours = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30; PH off");

        // Not a public holiday: Saturday's hours, which stores may not keep.
        Assert.Equal(new HoursState(true, Local(2026, 12, 26, 20, 30), false, true), HoursEvaluator.At(hours, Montreal, Local(2026, 12, 26, 12, 0)));
    }

    [Fact]
    public void A_holiday_s_own_hours_replace_its_day_s()
    {
        var hours = OpeningHours.Parse("Mo-Su 06:00-22:00; PH 09:00-17:00");

        Assert.Equal(new HoursState(false, Local(2026, 12, 25, 9, 0), true, false), HoursEvaluator.At(hours, Montreal, Local(2026, 12, 25, 8, 0)));
        Assert.Equal(Local(2026, 12, 25, 17, 0), HoursEvaluator.At(hours, Montreal, Local(2026, 12, 25, 12, 0)).ChangesAt);
        Assert.Equal(Local(2026, 12, 24, 22, 0), HoursEvaluator.At(hours, Montreal, Local(2026, 12, 24, 21, 0)).ChangesAt);
    }

    [Fact]
    public void A_rule_after_PH_off_opens_on_holidays_with_the_day_s_hours()
    {
        // Labour Day 2026, a Monday: "Mo-Su" names it after "PH off", so it's open, though its hours may not hold.
        var hours = OpeningHours.Parse("PH off; Mo-Su 06:00-22:00");

        Assert.Equal(new HoursState(true, Local(2026, 9, 7, 22, 0), true, true), HoursEvaluator.At(hours, Montreal, Local(2026, 9, 7, 10, 0)));
    }

    [Fact]
    public void A_saturday_holiday_has_saturday_s_hours_when_they_come_after_PH()
    {
        // Christmas 2027 is a Saturday: the Sa-Su rule replaces "Mo-Fr,PH"'s hours then.
        var hours = OpeningHours.Parse("Mo-Fr,PH 06:00-22:00; Sa-Su 08:00-20:00");

        Assert.Equal(new HoursState(false, Local(2027, 12, 25, 8, 0), true, true), HoursEvaluator.At(hours, Montreal, Local(2027, 12, 25, 7, 30)));
    }

    [Fact]
    public void The_day_before_a_holiday_spills_into_it_with_its_own_hours()
    {
        // Christmas Eve's 22:00-02:00 is Thursday's, and stays open into Christmas, whose own hours are off.
        var hours = OpeningHours.Parse("Mo-Su 22:00-02:00; PH off");

        Assert.Equal(new HoursState(true, Local(2026, 12, 25, 2, 0), true, false), HoursEvaluator.At(hours, Montreal, Local(2026, 12, 25, 1, 0)));
    }

    /// <summary>A local time in Montréal that's neither skipped nor repeated.</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, Montreal.GetUtcOffset(local));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static TimeZoneInfo Zone(bool fallback) =>
        fallback ? QuebecTime.Canadian("America/Toronto", TimeSpan.FromHours(-5), daylightTime: true) : Montreal;
}
