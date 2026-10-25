namespace Essence.Ranking;

/// <param name="OffKm">From the point to the route (its simplified line), straight.</param>
/// <param name="KmAlong">How far along the route the point is: where the route comes closest to it.</param>
public readonly record struct RoutePosition(double OffKm, double KmAlong);

/// <summary>
/// A route to find stations along. The full line OSRM sends gives the km along it; a simplification of it, which strays
/// at most <see cref="ToleranceMeters"/>, is what stations are measured against and what the map draws.
/// </summary>
public sealed class RouteLine
{
    /// <summary>How far the simplified line may stray from the route.</summary>
    public const double ToleranceMeters = 25;

    private readonly double[] _km;
    private readonly int[] _kept;
    private readonly LatLon[] _simplified;

    /// <param name="points">The route's full line, from the start.</param>
    /// <param name="lengthKm">
    /// The route's length as OSRM counts it: the km along are scaled to it when the line's own length differs.
    /// </param>
    public RouteLine(IReadOnlyList<LatLon> points, double? lengthKm = null)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("A route needs at least two points.", nameof(points));
        }

        _km = Polyline.CumulativeKm(points);
        if (lengthKm is > 0 && _km[^1] > 0)
        {
            var scale = lengthKm.Value / _km[^1];
            for (var i = 0; i < _km.Length; i++)
            {
                _km[i] *= scale;
            }
        }

        _kept = [.. Polyline.Simplify(points, ToleranceMeters)];
        _simplified = [.. _kept.Select(i => points[i])];
    }

    /// <summary>The simplified line, from the start.</summary>
    public IReadOnlyList<LatLon> Simplified => _simplified;

    public double LengthKm => _km[^1];

    /// <summary>Where a point lies along the route, or null when it's more than <paramref name="withinKm"/> off it.</summary>
    public RoutePosition? Locate(LatLon point, double withinKm)
    {
        var padLatitude = withinKm / Polyline.KmPerDegree;
        var padLongitude = withinKm / (Polyline.KmPerDegree * Math.Max(0.01, Math.Cos(point.Latitude * Math.PI / 180)));
        var bestKm = double.MaxValue;
        var bestSegment = -1;
        var bestAlong = 0.0;
        for (var i = 0; i + 1 < _simplified.Length; i++)
        {
            var (a, b) = (_simplified[i], _simplified[i + 1]);
            if (point.Latitude < Math.Min(a.Latitude, b.Latitude) - padLatitude
                || point.Latitude > Math.Max(a.Latitude, b.Latitude) + padLatitude
                || point.Longitude < Math.Min(a.Longitude, b.Longitude) - padLongitude
                || point.Longitude > Math.Max(a.Longitude, b.Longitude) + padLongitude)
            {
                continue;
            }

            var km = Segment.DistanceKm(point, a, b, out var along);
            if (km < bestKm)
            {
                (bestKm, bestSegment, bestAlong) = (km, i, along);
            }
        }

        if (bestSegment < 0 || bestKm > withinKm)
        {
            return null;
        }

        // The simplified points are points of the full line, so the km along between two of them follow the full line.
        var (from, to) = (_km[_kept[bestSegment]], _km[_kept[bestSegment + 1]]);
        return new RoutePosition(bestKm, from + bestAlong * (to - from));
    }
}
