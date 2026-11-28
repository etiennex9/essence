using System.Collections.Concurrent;
using Essence.Api.Endpoints;
using Essence.Hours;
using Essence.Ranking;

namespace Essence.Api;

/// <summary>
/// Each area's pattern and followers (<see cref="PricePattern.Find"/>), the costly part of the trends (50–130 ms measured,
/// up to 450 ms), computed once a day for each cell of a 1 km grid: the area of the cell's centre with the radius asked
/// for, so that moving the map doesn't ask for a new one at every step. Requests for a cell not computed yet wait for one
/// computation, not one each, and at most half the processors compute at once. A pattern from an earlier day is served
/// while the new one is computed in the background, from the immutable snapshot and history the request read, never from
/// its database context. At most <see cref="MaxCells"/> are kept, the least recently asked dropped first: about 1 KB
/// each, 5 MB at most.
/// </summary>
public sealed class TrendPatterns(ILogger<TrendPatterns> logger)
{
    public const int MaxCells = 5_000;

    /// <summary>The side of a cell, in degrees of latitude: about 1 km.</summary>
    private const double CellDegrees = 1 / 111.32;

    private readonly ConcurrentDictionary<Cell, Entry> _cells = new();
    private readonly SemaphoreSlim _computing = new(Math.Max(1, Environment.ProcessorCount / 2));

    public int Count => _cells.Count;

    /// <summary>The pattern of the area around (<paramref name="latitude"/>, <paramref name="longitude"/>), as of <paramref name="asOf"/>.</summary>
    public AreaPattern? Get(
        short fuelTypeId,
        bool costco,
        double radiusKm,
        double latitude,
        double longitude,
        Snapshot snapshot,
        PriceWindow window,
        DateTimeOffset asOf)
    {
        var cell = CellOf(fuelTypeId, costco, radiusKm, latitude, longitude);
        var (centreLatitude, centreLongitude) = cell.Centre;
        var zone = QuebecTime.ZoneFor(centreLatitude, centreLongitude);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, zone).DateTime);
        var now = asOf.UtcTicks;
        var entry = _cells.GetOrAdd(cell, _ => new Entry(day, new Lazy<AreaPattern?>(() => TryCompute(cell, snapshot, window, asOf, zone)), now));
        entry.LastAsked = now;
        if (entry.Day != day && entry.Pattern.IsValueCreated && Interlocked.Exchange(ref entry.Refreshing, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var pattern = Compute(cell, snapshot, window, asOf, zone);
                    _cells[cell] = new Entry(day, new Lazy<AreaPattern?>(pattern), now);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Couldn't compute an area's price pattern.");
                    entry.Refreshing = 0;
                }
            });
        }

        if (_cells.Count > MaxCells)
        {
            foreach (var old in _cells.OrderBy(c => c.Value.LastAsked).Take(_cells.Count - MaxCells * 9 / 10).Select(c => c.Key).ToList())
            {
                _cells.TryRemove(old, out _);
            }
        }

        return entry.Pattern.Value;
    }

    /// <summary>The cell a point is in: rows of <see cref="CellDegrees"/>, each split into about 1 km at its latitude.</summary>
    public static Cell CellOf(short fuelTypeId, bool costco, double radiusKm, double latitude, double longitude)
    {
        var row = (int)Math.Floor(latitude / CellDegrees);
        var width = CellDegrees / Math.Cos((row + 0.5) * CellDegrees * Math.PI / 180);
        return new Cell(fuelTypeId, costco, radiusKm, row, (int)Math.Floor(longitude / width));
    }

    /// <summary>The pattern, or null when it couldn't be computed (logged): the rest of the answer doesn't need it.</summary>
    private AreaPattern? TryCompute(Cell cell, Snapshot snapshot, PriceWindow window, DateTimeOffset asOf, TimeZoneInfo zone)
    {
        try
        {
            return Compute(cell, snapshot, window, asOf, zone);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't compute an area's price pattern.");
            return null;
        }
    }

    private AreaPattern? Compute(Cell cell, Snapshot snapshot, PriceWindow window, DateTimeOffset asOf, TimeZoneInfo zone)
    {
        _computing.Wait();
        try
        {
            var (latitude, longitude) = cell.Centre;
            var stations = StationEndpoints.Eligible(snapshot.Stations, cell.FuelTypeId, cell.Costco, latitude, longitude);
            var (area, _) = SearchArea.Select(stations, s => s.Km, cell.RadiusKm);
            return PricePattern.Find(TrendEndpoints.AreaOf(area.Select(s => s.Station.Id), window, cell.FuelTypeId), window.Coverage, asOf.UtcTicks, zone);
        }
        finally
        {
            _computing.Release();
        }
    }

    public readonly record struct Cell(short FuelTypeId, bool Costco, double RadiusKm, int Row, int Column)
    {
        public (double Latitude, double Longitude) Centre
        {
            get
            {
                var latitude = (Row + 0.5) * CellDegrees;
                var width = CellDegrees / Math.Cos(latitude * Math.PI / 180);
                return (latitude, (Column + 0.5) * width);
            }
        }
    }

    /// <param name="pattern">Computed by the first request that reads it, while the others asking for the cell wait for it.</param>
    private sealed class Entry(DateOnly day, Lazy<AreaPattern?> pattern, long lastAsked)
    {
        public DateOnly Day { get; } = day;

        public Lazy<AreaPattern?> Pattern { get; } = pattern;

        public long LastAsked { get; set; } = lastAsked;

        public int Refreshing;
    }
}
