using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// The pause between two requests to the same source in a run, drawn at random between <see cref="HoursOptions.MinPause"/>
/// and <see cref="HoursOptions.MaxPause"/> (2 to 3 s): a source asked many times (Shell's ~400 details) sees one request
/// every few seconds, never a burst nor a fixed beat. The clock is <see cref="TimeProvider"/>'s, the draws the
/// <see cref="Random"/>'s given.
/// </summary>
public sealed class HoursPacer(IOptions<HoursOptions> options, TimeProvider timeProvider, Random random)
{
    public Task PauseAsync(CancellationToken cancellationToken) => Task.Delay(Next(), timeProvider, cancellationToken);

    /// <summary>The next pause's length.</summary>
    public TimeSpan Next()
    {
        var o = options.Value;
        return o.MinPause + (o.MaxPause - o.MinPause) * random.NextDouble();
    }
}
