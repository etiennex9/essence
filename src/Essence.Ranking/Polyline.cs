namespace Essence.Ranking;

/// <summary>A point on the map, in degrees.</summary>
public readonly record struct LatLon(double Latitude, double Longitude);

/// <summary>Routes as lines: OSRM's encoded polylines, their length and their simplification.</summary>
public static class Polyline
{
    /// <summary>Kilometres per degree of latitude (and of longitude at the equator).</summary>
    internal const double KmPerDegree = 6371.0088 * Math.PI / 180;

    /// <summary>
    /// Decodes a polyline in Google's encoding, as OSRM sends it with <c>geometries=polyline6</c> (precision 6) or
    /// <c>polyline</c> (5).
    /// </summary>
    /// <exception cref="FormatException">The text isn't an encoded polyline.</exception>
    public static List<LatLon> Decode(string encoded, int precision = 6)
    {
        var factor = Math.Pow(10, precision);
        var points = new List<LatLon>(encoded.Length / 4);
        var index = 0;
        long latitude = 0, longitude = 0;
        while (index < encoded.Length)
        {
            latitude += ReadValue(encoded, ref index);
            longitude += ReadValue(encoded, ref index);
            points.Add(new LatLon(latitude / factor, longitude / factor));
        }

        return points;
    }

    /// <summary>The kilometres from the first point to each point, along the line (the first is 0).</summary>
    public static double[] CumulativeKm(IReadOnlyList<LatLon> points)
    {
        var km = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            km[i] = km[i - 1] + Geo.DistanceKm(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
        }

        return km;
    }

    /// <summary>
    /// Douglas–Peucker: the points to keep so that the line strays at most <paramref name="toleranceMeters"/> from the
    /// original, as indices into it, in order. The first and last points are always kept.
    /// </summary>
    public static List<int> Simplify(IReadOnlyList<LatLon> points, double toleranceMeters)
    {
        if (points.Count <= 2)
        {
            return [.. Enumerable.Range(0, points.Count)];
        }

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var toleranceKm = toleranceMeters / 1000;

        // A stack rather than recursion: a long route has tens of thousands of points.
        var spans = new Stack<(int First, int Last)>();
        spans.Push((0, points.Count - 1));
        while (spans.Count > 0)
        {
            var (first, last) = spans.Pop();
            var farthest = -1;
            var farthestKm = toleranceKm;
            for (var i = first + 1; i < last; i++)
            {
                var km = Segment.DistanceKm(points[i], points[first], points[last], out _);
                if (km > farthestKm)
                {
                    (farthest, farthestKm) = (i, km);
                }
            }

            if (farthest >= 0)
            {
                keep[farthest] = true;
                spans.Push((first, farthest));
                spans.Push((farthest, last));
            }
        }

        var kept = new List<int>();
        for (var i = 0; i < keep.Length; i++)
        {
            if (keep[i])
            {
                kept.Add(i);
            }
        }

        return kept;
    }

    private static long ReadValue(string encoded, ref int index)
    {
        long result = 0;
        var shift = 0;
        int chunk;
        do
        {
            if (index >= encoded.Length || shift > 60)
            {
                throw new FormatException("The polyline ends in the middle of a value.");
            }

            chunk = encoded[index++] - 63;
            if (chunk is < 0 or > 63)
            {
                throw new FormatException("The polyline has a character outside its encoding.");
            }

            result |= (long)(chunk & 0x1f) << shift;
            shift += 5;
        }
        while (chunk >= 0x20);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }
}

/// <summary>Distances between a point and a segment, on a flat projection around the point: fine at a few km.</summary>
internal static class Segment
{
    /// <summary>The kilometres from <paramref name="point"/> to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    /// <param name="along">Where the closest point of the segment is: 0 at <paramref name="a"/>, 1 at <paramref name="b"/>.</param>
    public static double DistanceKm(LatLon point, LatLon a, LatLon b, out double along)
    {
        var cos = Math.Cos(point.Latitude * Math.PI / 180);
        var (ax, ay) = ((a.Longitude - point.Longitude) * cos, a.Latitude - point.Latitude);
        var (bx, by) = ((b.Longitude - point.Longitude) * cos, b.Latitude - point.Latitude);
        var (dx, dy) = (bx - ax, by - ay);
        var lengthSquared = dx * dx + dy * dy;
        along = lengthSquared == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / lengthSquared, 0, 1);
        var (x, y) = (ax + along * dx, ay + along * dy);
        return Math.Sqrt(x * x + y * y) * Polyline.KmPerDegree;
    }
}
