using Essence.Hours;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Ingest.Tests;

public sealed class HoursScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);

    private HoursSchedule Schedule(int seed = 42) => new(Options.Create(new HoursOptions()), _time, new Random(seed));

    private static TimeSpan LocalTime(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, QuebecTime.Eastern).TimeOfDay;

    [Fact]
    public void A_source_that_never_succeeded_runs_soon_after_the_start_a_minute_after_the_one_before()
    {
        var schedule = Schedule();
        var never = new SourceRuns(null, null, 0);

        Assert.Equal(Start, schedule.NextRun(never, Start, 0));
        Assert.Equal(Start.AddMinutes(1), schedule.NextRun(never, Start, 1));
        Assert.Equal(Start.AddMinutes(2), schedule.NextRun(never, Start, 2));
    }

    [Fact]
    public void After_a_success_it_runs_a_week_later_at_a_random_minute_of_that_night()
    {
        // Wednesday 2026-11-11 at 03:12 in Québec (EST, UTC-5).
        var succeededAt = new DateTimeOffset(2026, 11, 11, 8, 12, 0, TimeSpan.Zero);

        var next = Schedule().NextRun(new SourceRuns(succeededAt, succeededAt, 0), Start, 1);

        // Wednesday 2026-11-18, 01:00 to 05:00 EST.
        Assert.InRange(next, new DateTimeOffset(2026, 11, 18, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 18, 9, 59, 0, TimeSpan.Zero));
        Assert.Equal(0, next.Second);
        Assert.Equal(0, next.Ticks % TimeSpan.TicksPerMinute);
    }

    [Fact]
    public void The_minute_is_drawn_again_each_week()
    {
        var schedule = Schedule();
        var succeededAt = new DateTimeOffset(2026, 11, 11, 8, 12, 0, TimeSpan.Zero);
        var minutes = new List<TimeSpan>();
        for (var week = 0; week < 8; week++)
        {
            succeededAt = schedule.NextRun(new SourceRuns(succeededAt, succeededAt, 0), Start, 1);
            minutes.Add(LocalTime(succeededAt));
            Assert.InRange(LocalTime(succeededAt), TimeSpan.FromHours(1), TimeSpan.FromHours(5) - TimeSpan.FromMinutes(1));
        }

        Assert.True(minutes.Distinct().Count() > 4, string.Join(", ", minutes));
    }

    [Fact]
    public void The_night_daylight_time_starts_the_window_is_three_hours_and_never_in_the_hour_skipped()
    {
        // Sunday 2026-03-01 at 03:00 EST; on Sunday 2026-03-08 the clock goes from 02:00 EST to 03:00 EDT.
        var succeededAt = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var windowStart = new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero); // 01:00 EST
        var windowEnd = new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.Zero); // 05:00 EDT

        var draws = Enumerable.Range(0, 300).Select(seed => Schedule(seed).NextRun(new SourceRuns(succeededAt, succeededAt, 0), succeededAt, 1)).ToList();

        Assert.All(draws, d => Assert.True(d >= windowStart && d < windowEnd, $"{d:O}"));
        Assert.All(draws, d => Assert.False(LocalTime(d) >= TimeSpan.FromHours(2) && LocalTime(d) < TimeSpan.FromHours(3), $"{d:O}"));
        Assert.Contains(draws, d => d >= windowEnd.AddHours(-1));
    }

    [Fact]
    public void The_night_daylight_time_ends_the_window_is_five_hours()
    {
        // Sunday 2026-10-25 at 03:00 EDT; on Sunday 2026-11-01 the clock goes from 02:00 EDT back to 01:00 EST.
        var succeededAt = new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero);
        var windowStart = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero); // 01:00 EDT
        var windowEnd = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero); // 05:00 EST

        var draws = Enumerable.Range(0, 300).Select(seed => Schedule(seed).NextRun(new SourceRuns(succeededAt, succeededAt, 0), succeededAt, 1)).ToList();

        Assert.All(draws, d => Assert.True(d >= windowStart && d < windowEnd, $"{d:O}"));
        Assert.Contains(draws, d => d < windowStart.AddHours(1));
        Assert.Contains(draws, d => d >= windowEnd.AddHours(-1));
    }

    [Fact]
    public void A_failure_is_tried_again_after_15_minutes_then_1_hour_then_every_6_hours_until_a_success()
    {
        var schedule = Schedule();
        var succeededAt = Start.AddDays(-7);
        var failedAt = Start.AddHours(2);

        TimeSpan After(int failures) => schedule.NextRun(new SourceRuns(succeededAt, failedAt, failures), Start, 1) - failedAt;

        Assert.Equal(
            [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(6), TimeSpan.FromHours(6)],
            Enumerable.Range(1, 5).Select(After));
    }

    [Fact]
    public void A_source_that_never_succeeded_backs_off_too()
    {
        var failedAt = Start.AddMinutes(1);

        Assert.Equal(failedAt.AddMinutes(15), Schedule().NextRun(new SourceRuns(null, failedAt, 1), Start, 1));
    }

    [Fact]
    public void A_run_missed_while_the_worker_was_down_runs_soon_after_the_start()
    {
        var succeededAt = Start.AddDays(-20);

        Assert.Equal(Start.AddMinutes(1), Schedule().NextRun(new SourceRuns(succeededAt, succeededAt, 0), Start, 1));
    }

    [Fact]
    public void The_backoff_can_be_set()
    {
        var options = new HoursOptions { Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)] };
        var schedule = new HoursSchedule(Options.Create(options), _time, new Random(1));

        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)], Enumerable.Range(1, 3).Select(schedule.RetryAfter));
        Assert.Equal(HoursOptions.DefaultBackoff, new HoursOptions().BackoffSteps);
    }
}
