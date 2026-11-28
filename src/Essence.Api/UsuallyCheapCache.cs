using System.Diagnostics;
using Essence.Hours;
using Essence.Ranking;

namespace Essence.Api;

/// <summary>
/// The stations usually cheap for each fuel (<see cref="CheapStations.Find"/>): about half a second of work for every
/// station in Québec, done once a Québec day per fuel, the first time it's asked. Requests asking while it's computed wait
/// for that one computation. Once a day has passed (a new date in Québec, or 24 hours), the last answer is served while the
/// next is computed in the background, from the immutable snapshot and history the request read, never from its database
/// context. A computation that fails is logged; the first is tried again by the next request, a refresh
/// <see cref="RetryAfterFailure"/> later.
/// </summary>
public sealed class UsuallyCheapCache(TimeProvider time, ILogger<UsuallyCheapCache> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>After a refresh failed, the last answer is served this long before another is tried.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private readonly Dictionary<short, Fuel> _fuels = [];

    /// <summary>
    /// The fuel's usually cheap stations; null before the first import. Waits only when none was computed yet: otherwise
    /// the last answer, even a day old while the next is computed.
    /// </summary>
    public Task<UsualCheapness?> GetAsync(short fuelTypeId, Snapshot snapshot, PriceWindow window, CancellationToken cancellationToken)
    {
        if (window.LastImport is null)
        {
            return Task.FromResult<UsualCheapness?>(null);
        }

        var now = time.GetUtcNow();
        var day = QuebecDay(now);
        Task<UsualCheapness?> pending;
        lock (_fuels)
        {
            if (!_fuels.TryGetValue(fuelTypeId, out var fuel))
            {
                _fuels[fuelTypeId] = fuel = new Fuel();
            }

            if (fuel.Current is { } current)
            {
                if ((current.Day != day || now - current.At >= MaxAge) && fuel.Pending is null && !(now < fuel.RetryAt))
                {
                    Start(fuel, fuelTypeId, snapshot, window, now, day);
                }

                return Task.FromResult(current.Found);
            }

            pending = fuel.Pending ?? Start(fuel, fuelTypeId, snapshot, window, now, day);
        }

        return pending.WaitAsync(cancellationToken);
    }

    /// <summary>The computation, on the thread pool; its answer becomes the fuel's when it's in. Called under the lock.</summary>
    private Task<UsualCheapness?> Start(Fuel fuel, short fuelTypeId, Snapshot snapshot, PriceWindow window, DateTimeOffset now, DateOnly day)
    {
        var task = Task.Run(() => Compute(fuelTypeId, snapshot, window, now));
        fuel.Pending = task;
        _ = task.ContinueWith(
            done =>
            {
                lock (_fuels)
                {
                    if (done.IsCompletedSuccessfully)
                    {
                        fuel.Current = new Answer(day, now, done.Result);
                    }
                    else
                    {
                        logger.LogWarning(done.Exception, "Couldn't find the usually cheap stations.");
                        fuel.RetryAt = time.GetUtcNow() + RetryAfterFailure;
                    }

                    fuel.Pending = null;
                }
            },
            TaskScheduler.Default);
        return task;
    }

    private UsualCheapness? Compute(short fuelTypeId, Snapshot snapshot, PriceWindow window, DateTimeOffset now)
    {
        var watch = Stopwatch.StartNew();
        var candidates = new List<CheapCandidate>();
        foreach (var station in snapshot.Stations)
        {
            if (station.Status == Eligibility.Operating && window.Of(station.Id, fuelTypeId) is { } prices)
            {
                candidates.Add(new CheapCandidate(station.Id, station.Latitude, station.Longitude, station.MembersOnly, prices));
            }
        }

        var found = CheapStations.Find(candidates, window.Coverage, now);
        logger.LogInformation(
            "Found the usually cheap stations for fuel {FuelTypeId}: {Cheap} of {Rated} rated, over {Days} days, in {Milliseconds} ms.",
            fuelTypeId, found?.Stations.Count ?? 0, found?.Rated ?? 0, found?.Days ?? 0, watch.ElapsedMilliseconds);
        return found;
    }

    private static DateOnly QuebecDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, QuebecTime.Eastern).DateTime);

    private sealed record Answer(DateOnly Day, DateTimeOffset At, UsualCheapness? Found);

    private sealed class Fuel
    {
        public Answer? Current { get; set; }

        public Task<UsualCheapness?>? Pending { get; set; }

        /// <summary>A refresh failed: none is tried again before this.</summary>
        public DateTimeOffset? RetryAt { get; set; }
    }
}
