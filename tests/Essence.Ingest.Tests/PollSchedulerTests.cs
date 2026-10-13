using Essence.Ingest.Feed;
using Essence.Ingest.Scheduling;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Ingest.Tests;

public sealed class PollSchedulerTests
{
    // The Régie generates files at :00:01, :05:01, …
    private static readonly DateTimeOffset Generated = new(2026, 10, 12, 12, 0, 1, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Generated.AddSeconds(64));
    private readonly PollScheduler _scheduler;

    public PollSchedulerTests()
    {
        _scheduler = new PollScheduler(Options.Create(new FeedOptions { Jitter = TimeSpan.Zero }), _time);
    }

    private static DateTimeOffset At(int minutes, int seconds) => Generated.AddMinutes(minutes).AddSeconds(seconds);

    /// <summary>Moves the clock to the planned time and records a poll there, like the worker does.</summary>
    private PollPlan PollAt(DateTimeOffset at)
    {
        _time.SetUtcNow(at);
        _scheduler.OnPolled(at);
        return _scheduler.NextPoll();
    }

    [Fact]
    public void Without_history_it_polls_immediately_then_every_slow_interval()
    {
        _scheduler.Initialize(null, []);

        Assert.Equal(new PollPlan(_time.GetUtcNow(), PollPhase.Immediate), _scheduler.NextPoll());

        var now = _time.GetUtcNow();
        Assert.Equal(new PollPlan(now.AddSeconds(60), PollPhase.Fallback), PollAt(now));
    }

    [Fact]
    public void After_an_import_it_sleeps_until_the_next_window_opens()
    {
        _scheduler.Initialize(null, []);
        _scheduler.OnPolled(_time.GetUtcNow());
        _scheduler.OnNewVersion(Generated, delaySeconds: null);

        // Next file at 12:05:01, window opens 40 s (initial delay) later.
        Assert.Equal(new PollPlan(At(5, 40), PollPhase.AwaitingWindow), _scheduler.NextPoll());
    }

    [Fact]
    public void Inside_the_window_it_polls_every_fast_interval()
    {
        _scheduler.Initialize(Generated, []);

        Assert.Equal(new PollPlan(At(5, 50), PollPhase.Window), PollAt(At(5, 40)));
        Assert.Equal(new PollPlan(At(6, 0), PollPhase.Window), PollAt(At(5, 50)));
        Assert.Equal(new PollPlan(At(8, 39), PollPhase.Window), PollAt(At(8, 29)));
    }

    [Fact]
    public void After_three_minutes_without_a_file_it_falls_back_to_slow_polling_until_the_next_window()
    {
        _scheduler.Initialize(Generated, []);

        // The window [12:05:41, 12:08:41) closed; the next one opens at 12:10:41.
        Assert.Equal(new PollPlan(At(9, 41), PollPhase.Fallback), PollAt(At(8, 41)));
        Assert.Equal(new PollPlan(At(10, 40), PollPhase.Fallback), PollAt(At(9, 41)));
        Assert.Equal(new PollPlan(At(10, 50), PollPhase.Window), PollAt(At(10, 40)));
    }

    [Fact]
    public void A_new_file_ends_the_window_and_realigns_on_its_generated_at()
    {
        _scheduler.Initialize(Generated, []);
        PollAt(At(5, 40));

        _scheduler.OnNewVersion(At(5, 3), delaySeconds: 47);

        // Next slot 12:10:04; learned delay is now the single sample, 47 s.
        Assert.Equal(TimeSpan.FromSeconds(47), _scheduler.LearnedDelay);
        Assert.Equal(new PollPlan(At(10, 50), PollPhase.AwaitingWindow), _scheduler.NextPoll());
    }

    [Fact]
    public void Missed_slots_roll_forward_to_the_current_window()
    {
        // Restart 23.5 min after the last import: the window for the 12:20:01 file is open until 12:23:41.
        _scheduler.Initialize(Generated, []);
        _time.SetUtcNow(At(23, 30));

        Assert.Equal(PollPhase.Immediate, _scheduler.NextPoll().Phase);
        Assert.Equal(new PollPlan(At(23, 40), PollPhase.Window), PollAt(At(23, 30)));
        Assert.Equal(new PollPlan(At(24, 45), PollPhase.Fallback), PollAt(At(23, 45)));
        Assert.Equal(new PollPlan(At(25, 40), PollPhase.Fallback), PollAt(At(24, 45)));
        Assert.Equal(new PollPlan(At(25, 50), PollPhase.Window), PollAt(At(25, 40)));
    }

    [Fact]
    public void After_downtime_between_windows_it_polls_immediately()
    {
        _scheduler.Initialize(Generated, []);
        _time.SetUtcNow(At(24, 0));

        Assert.Equal(new PollPlan(At(24, 0), PollPhase.Immediate), _scheduler.NextPoll());
    }

    [Fact]
    public void Starting_before_the_window_waits_for_it()
    {
        _scheduler.Initialize(Generated, []);
        _time.SetUtcNow(At(2, 0));

        Assert.Equal(new PollPlan(At(5, 40), PollPhase.AwaitingWindow), _scheduler.NextPoll());
    }

    [Fact]
    public void The_learned_delay_is_the_20th_percentile_of_recent_delays()
    {
        _scheduler.Initialize(Generated, [50, 45, 90, 60, 70, 80, 55, 65, 75, 85]);

        // Sorted: 45, 50, 55, …; nearest rank for p20 of 10 samples is the 2nd.
        Assert.Equal(TimeSpan.FromSeconds(50), _scheduler.LearnedDelay);
        _time.SetUtcNow(At(2, 0));
        Assert.Equal(At(5, 50), _scheduler.NextPoll().At);
    }

    [Theory]
    [InlineData(new double[0], 40)]
    [InlineData(new double[] { 5, 6, 7 }, 20)]
    [InlineData(new double[] { 200, 210, 250 }, 120)]
    [InlineData(new double[] { 300, 3600, -1 }, 40)] // Not availability delays: ignored.
    public void The_learned_delay_starts_at_40_s_and_is_clamped(double[] delays, int expectedSeconds)
    {
        _scheduler.Initialize(Generated, delays);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), _scheduler.LearnedDelay);
    }

    [Fact]
    public void Only_the_most_recent_delays_are_kept()
    {
        _scheduler.Initialize(Generated, Enumerable.Repeat(30.0, 50));
        Assert.Equal(TimeSpan.FromSeconds(30), _scheduler.LearnedDelay);

        for (var i = 0; i < 50; i++)
        {
            _scheduler.OnNewVersion(Generated.AddMinutes(5 * (i + 1)), 90);
        }

        Assert.Equal(TimeSpan.FromSeconds(90), _scheduler.LearnedDelay);
    }

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var scheduler = new PollScheduler(Options.Create(new FeedOptions()), _time);
        scheduler.Initialize(Generated, []);
        _time.SetUtcNow(At(2, 0));

        for (var i = 0; i < 100; i++)
        {
            var at = scheduler.NextPoll().At;
            Assert.InRange(at, At(5, 38), At(5, 42));
        }
    }
}
