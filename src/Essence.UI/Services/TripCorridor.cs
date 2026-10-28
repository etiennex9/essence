using Clipper2Lib;

namespace Essence.UI.Services;

/// <summary>
/// The corridor a trip's stations were looked for in, as the map draws it: every point within the half-width of the
/// route, as one shape filled once. (A line that wide tinted twice where the route came back near itself, and broke into
/// spikes at sharp turns.)
/// </summary>
public static class TripCorridor
{
    /// <summary>Metres per degree of latitude, on Geo's sphere.</summary>
    private const double MetersPerDegree = 6_371_008.8 * Math.PI / 180;

    /// <summary>
    /// The corridor around <paramref name="path"/> ([longitude, latitude] pairs), <paramref name="halfWidthMeters"/> to
    /// either side and around both ends, as GeoJSON MultiPolygon coordinates: polygons of closed rings, each outline
    /// (counterclockwise) then its holes (clockwise: a route that loops round leaves one). Empty without a path or width.
    /// </summary>
    /// <remarks>
    /// It's drawn, not measured: on a plane at the route's middle latitude (east–west distances stray from true by under
    /// 5 % over a trip within Québec), the route simplified to within a 25th of the half-width, and its curves kept
    /// within a 100th of it, which keeps it to a few hundred points for the browser to compute and the map to draw.
    /// </remarks>
    public static double[][][][] Polygons(double[][] path, double halfWidthMeters)
    {
        if (path.Length == 0 || !(halfWidthMeters > 0))
        {
            return [];
        }

        var (south, north) = (path.Min(p => p[1]), path.Max(p => p[1]));
        var plane = new Plane(path[0][0], (south + north) / 2);
        var line = new Path64(path.Length);
        foreach (var point in path)
        {
            line.Add(plane.ToPoint(point));
        }

        line = Clipper.SimplifyPath(line, halfWidthMeters / 25, isClosedPath: false);
        var offset = new ClipperOffset(arcTolerance: halfWidthMeters / 100);
        offset.AddPath(line, JoinType.Round, EndType.Round);
        var tree = new PolyTree64();
        offset.Execute(halfWidthMeters, tree);

        var polygons = new List<double[][][]>();
        AddPolygons(tree, plane, polygons);
        return [.. polygons];
    }

    /// <summary>The outlines under <paramref name="parent"/>, each with its holes, and the outlines inside those holes.</summary>
    private static void AddPolygons(PolyPathBase parent, Plane plane, List<double[][][]> polygons)
    {
        foreach (PolyPath64 outline in parent)
        {
            var rings = new List<double[][]> { Ring(outline.Polygon!, plane, counterclockwise: true) };
            foreach (PolyPath64 hole in outline)
            {
                rings.Add(Ring(hole.Polygon!, plane, counterclockwise: false));
                AddPolygons(hole, plane, polygons);
            }

            polygons.Add([.. rings]);
        }
    }

    /// <summary>A ring in [longitude, latitude] pairs to about a metre, closed, turning the way GeoJSON wants it.</summary>
    private static double[][] Ring(Path64 path, Plane plane, bool counterclockwise)
    {
        var ring = new double[path.Count + 1][];
        for (var i = 0; i < path.Count; i++)
        {
            ring[i] = plane.ToLonLat(path[i]);
        }

        ring[^1] = ring[0];
        if (Clipper.Area(path) > 0 != counterclockwise)
        {
            Array.Reverse(ring);
        }

        return ring;
    }

    /// <summary>Metres east and north of a point, the scale of a degree of longitude taken at one latitude.</summary>
    private readonly struct Plane(double longitude, double latitude)
    {
        private readonly double _metersPerDegreeEast = MetersPerDegree * Math.Cos(latitude * Math.PI / 180);

        public Point64 ToPoint(double[] lonLat) =>
            new((lonLat[0] - longitude) * _metersPerDegreeEast, (lonLat[1] - latitude) * MetersPerDegree);

        public double[] ToLonLat(Point64 point) =>
        [
            Math.Round(longitude + point.X / _metersPerDegreeEast, 5),
            Math.Round(latitude + point.Y / MetersPerDegree, 5),
        ];
    }
}
