using Essence.Ingest.Hours;
using Microsoft.Extensions.Time.Testing;
using static Essence.Ingest.Tests.HoursFixtures;

namespace Essence.Ingest.Tests;

public sealed class HoursPacerTests
{
    [Fact]
    public void Pauses_are_drawn_between_2_and_3_seconds_and_differ()
    {
        var pacer = new HoursPacer(Options(), new FakeTimeProvider(), new Random(7));

        var pauses = Enumerable.Range(0, 50).Select(_ => pacer.Next()).ToList();

        Assert.All(pauses, p => Assert.InRange(p, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)));
        Assert.True(pauses.Distinct().Count() > 40);
    }

    [Fact]
    public async Task A_pause_lasts_until_its_time_has_passed_on_the_clock_given()
    {
        var clock = new FakeTimeProvider();
        var pacer = new HoursPacer(Options(o => (o.MinPause, o.MaxPause) = (TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2))), clock, new Random(7));

        var pause = pacer.PauseAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.False(pause.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(0.1));

        await pause.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
