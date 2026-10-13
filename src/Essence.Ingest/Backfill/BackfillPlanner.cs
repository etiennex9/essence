using System.Globalization;

namespace Essence.Ingest.Backfill;

/// <param name="Slots">Missed publication slots still within reach, oldest first.</param>
/// <param name="TooOld">Missed slots older than <see cref="BackfillOptions.MaxAge"/>, which can't be recovered.</param>
public sealed record BackfillPlan(IReadOnlyList<DateTimeOffset> Slots, int TooOld);

/// <summary>Works out which versions were missed and what their xlsx files are called. Pure logic.</summary>
public static class BackfillPlanner
{
    /// <summary>
    /// Seconds after the slot to try, in order. Files are generated at :00.9–:01.8 after the slot and named after that time
    /// truncated to the second: :01 most of the time, :00 otherwise (measured over an hour).
    /// </summary>
    private static readonly int[] SecondsToTry = [1, 0, 2];

    /// <summary>The slots strictly between two versions, on the publication grid (e.g. every 5 minutes on the clock).</summary>
    public static BackfillPlan MissedSlots(
        DateTimeOffset lastGeneratedAt, DateTimeOffset newGeneratedAt, DateTimeOffset now, TimeSpan interval, TimeSpan maxAge)
    {
        var first = RoundToGrid(lastGeneratedAt, interval) + interval;
        var end = RoundToGrid(newGeneratedAt, interval);
        if (end <= first)
        {
            return new BackfillPlan([], 0);
        }

        // Jump over the slots past retention instead of looping through them (an outage can last days),
        // and stop at now: a bogus generated_at far in the future can't make the loop long either.
        var slot = first;
        long tooOld = 0;
        var oldest = now - maxAge;
        if (slot < oldest)
        {
            var skip = CeilingDivide(oldest - first, interval);
            tooOld = Math.Min(skip, CeilingDivide(end - first, interval));
            slot = first + TimeSpan.FromTicks(interval.Ticks * skip);
        }

        var slots = new List<DateTimeOffset>();
        for (; slot < end && slot <= now; slot += interval)
        {
            slots.Add(slot);
        }

        return new BackfillPlan(slots, (int)tooOld);
    }

    /// <summary>The file names to try for a slot, in order, each with the generated_at it stands for.</summary>
    public static IEnumerable<(string FileName, DateTimeOffset GeneratedAt)> FileCandidates(DateTimeOffset slot) =>
        SecondsToTry.Select(seconds =>
        {
            var generatedAt = slot.ToUniversalTime().AddSeconds(seconds);
            return ($"stations-{generatedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.xlsx", generatedAt);
        });

    /// <summary>Nearest grid point, so a file generated a little before or after its slot still maps to it.</summary>
    private static DateTimeOffset RoundToGrid(DateTimeOffset value, TimeSpan interval)
    {
        var ticks = value.UtcTicks;
        return new DateTimeOffset((ticks + interval.Ticks / 2) / interval.Ticks * interval.Ticks, TimeSpan.Zero);
    }

    private static long CeilingDivide(TimeSpan value, TimeSpan interval) => (value.Ticks + interval.Ticks - 1) / interval.Ticks;
}
