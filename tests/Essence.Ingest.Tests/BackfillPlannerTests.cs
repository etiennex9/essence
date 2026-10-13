using Essence.Ingest.Backfill;

namespace Essence.Ingest.Tests;

public sealed class BackfillPlannerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(70);

    private static DateTimeOffset At(int hour, int minute, double seconds = 0) =>
        new DateTimeOffset(2026, 10, 12, hour, minute, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public void Consecutive_versions_miss_nothing()
    {
        var plan = BackfillPlanner.MissedSlots(At(12, 0, 1.2), At(12, 5, 0.9), At(12, 6), Interval, MaxAge);

        Assert.Empty(plan.Slots);
        Assert.Equal(0, plan.TooOld);
    }

    [Fact]
    public void Lists_the_slots_between_two_versions()
    {
        var plan = BackfillPlanner.MissedSlots(At(12, 0, 1.2), At(12, 20, 0.9), At(12, 21), Interval, MaxAge);

        Assert.Equal([At(12, 5), At(12, 10), At(12, 15)], plan.Slots);
        Assert.Equal(0, plan.TooOld);
    }

    [Fact]
    public void Versions_generated_just_before_their_slot_round_to_it()
    {
        var plan = BackfillPlanner.MissedSlots(At(11, 59, 59.9), At(12, 9, 59.5), At(12, 11), Interval, MaxAge);

        Assert.Equal([At(12, 5)], plan.Slots);
    }

    [Fact]
    public void Slots_past_retention_are_counted_not_listed()
    {
        // Down from 10:00 to 12:20; at 12:21:10 the oldest file still within 70 min is 11:15.
        var plan = BackfillPlanner.MissedSlots(At(10, 0, 1), At(12, 20, 1), At(12, 21, 10), Interval, MaxAge);

        Assert.Equal(At(11, 15), plan.Slots[0]);
        Assert.Equal(At(12, 15), plan.Slots[^1]);
        Assert.Equal(13, plan.Slots.Count);
        Assert.Equal(14, plan.TooOld); // 10:05 … 11:10
    }

    [Fact]
    public void A_gap_entirely_past_retention_lists_nothing()
    {
        var plan = BackfillPlanner.MissedSlots(At(8, 0, 1), At(8, 20, 1), At(12, 0), Interval, MaxAge);

        Assert.Empty(plan.Slots);
        Assert.Equal(3, plan.TooOld);
    }

    [Fact]
    public void A_long_outage_doesnt_loop_through_every_slot()
    {
        var plan = BackfillPlanner.MissedSlots(At(12, 0, 1).AddYears(-10), At(12, 20, 1), At(12, 21), Interval, MaxAge);

        Assert.Equal(13, plan.Slots.Count); // 11:15 … 12:15
        Assert.True(plan.TooOld > 1_000_000);
    }

    [Fact]
    public void A_bogus_future_version_only_lists_slots_up_to_now()
    {
        var plan = BackfillPlanner.MissedSlots(At(12, 0, 1), At(12, 0, 1).AddYears(80), At(12, 16), Interval, MaxAge);

        Assert.Equal([At(12, 5), At(12, 10), At(12, 15)], plan.Slots);
    }

    [Fact]
    public void File_names_try_the_usual_seconds_first()
    {
        var candidates = BackfillPlanner.FileCandidates(At(12, 5)).ToList();

        Assert.Equal(
            [
                ("stations-20261012120501.xlsx", At(12, 5, 1)),
                ("stations-20261012120500.xlsx", At(12, 5, 0)),
                ("stations-20261012120502.xlsx", At(12, 5, 2)),
            ],
            candidates);
    }
}
