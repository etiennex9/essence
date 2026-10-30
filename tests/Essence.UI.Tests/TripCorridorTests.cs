using Essence.UI.Services;

namespace Essence.UI.Tests;

public class TripCorridorTests
{
    // Routes are laid out in km east and north of a point in Montréal.
    private const double Latitude0 = 45.5;
    private const double Longitude0 = -73.6;
    private const double KmPerDegree = 6371.0088 * Math.PI / 180;

    [Fact]
    public void A_straight_route_is_one_shape_the_half_width_to_either_side_and_round_at_both_ends()
    {
        var corridor = TripCorridor.Polygons(Route((0, 0), (5, 0), (12, 0), (20, 0)), 2000);

        var polygon = Assert.Single(corridor);
        Assert.Single(polygon);
        Assert.True(Inside(corridor, 10, 1.9));
        Assert.True(Inside(corridor, 10, -1.9));
        Assert.True(Inside(corridor, 21.9, 0));
        Assert.True(Inside(corridor, -1.9, 0));
        Assert.False(Inside(corridor, 10, 2.1));
        Assert.False(Inside(corridor, 22.1, 0));
        // Round, not square: past the end's corner is more than 2 km from the route.
        Assert.False(Inside(corridor, 21.9, 1.9));
    }

    [Fact]
    public void A_route_that_comes_back_near_itself_is_one_outline_that_never_crosses_itself()
    {
        // Out and back 1 km apart, with a 2 km half-width: the two sides overlap, and are drawn as one.
        var corridor = TripCorridor.Polygons(Route((0, 0), (20, 0), (20.5, 0.5), (20, 1), (0, 1)), 2000);

        var polygon = Assert.Single(corridor);
        var outline = Assert.Single(polygon);
        Assert.True(IsSimple(outline));
        Assert.True(Inside(corridor, 10, 0.5));
        Assert.True(Inside(corridor, 10, 2.9));
        Assert.True(Inside(corridor, 10, -1.9));
        Assert.False(Inside(corridor, 10, 3.1));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(8000)]
    public void A_start_on_the_wrong_carriageway_keeps_the_corridor_round_its_detour(double halfWidthMeters)
    {
        // Snapped to the far carriageway of an autoroute: 8 km the wrong way, round the interchange, then back past the
        // start and on, 34 m from the way out.
        var corridor = TripCorridor.Polygons(
            Route((0, 0.017), (-7.6, 0.017), (-8, 0.1), (-8, -0.1), (-7.6, -0.017), (60, -0.017)), halfWidthMeters);

        Assert.True(Inside(corridor, -8, 0));
        Assert.True(Inside(corridor, -8 - 0.9 * halfWidthMeters / 1000, 0));
    }

    [Fact]
    public void A_route_that_loops_round_leaves_a_hole_inside_the_loop()
    {
        var corridor = TripCorridor.Polygons(Route((0, 0), (40, 0), (40, 40), (0, 40), (0, 3)), 2000);

        var polygon = Assert.Single(corridor);
        Assert.Equal(2, polygon.Length);
        Assert.False(Inside(corridor, 20, 20));
        Assert.True(Inside(corridor, 20, 1));
        Assert.True(Inside(corridor, 38.5, 20));
    }

    [Fact]
    public void Rings_are_closed_and_turn_the_GeoJSON_way()
    {
        var corridor = TripCorridor.Polygons(Route((0, 0), (40, 0), (40, 40), (0, 40), (0, 3)), 2000);

        foreach (var polygon in corridor)
        {
            Assert.All(polygon, ring => Assert.Equal(ring[0], ring[^1]));
            Assert.True(SignedArea(polygon[0]) > 0, "An outline turns counterclockwise.");
            Assert.All(polygon[1..], hole => Assert.True(SignedArea(hole) < 0, "A hole turns clockwise."));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void There_is_nothing_to_draw_without_a_width(double halfWidthMeters) =>
        Assert.Empty(TripCorridor.Polygons(Route((0, 0), (20, 0)), halfWidthMeters));

    [Fact]
    public void There_is_nothing_to_draw_without_a_route() => Assert.Empty(TripCorridor.Polygons([], 2000));

    [Fact]
    public void A_long_winding_route_comes_down_to_a_few_hundred_points()
    {
        // 400 km north-east in 4,000 points that wander 30 m either side of a road that curves every 20 km.
        var points = Enumerable.Range(0, 4001)
            .Select(i => i * 0.1)
            .Select(km => (km * 0.8 + 3 * Math.Sin(km / 20 * Math.PI), km * 0.6 + 0.03 * Math.Sin(km * 7)))
            .ToArray();

        var corridor = TripCorridor.Polygons(Route(points), 4000);

        Assert.Single(corridor);
        Assert.InRange(corridor.Sum(polygon => polygon.Sum(ring => ring.Length)), 50, 1000);
        Assert.True(Inside(corridor, points[2000].Item1 + 3.5, points[2000].Item2));
    }

    private static double[][] Route(params (double EastKm, double NorthKm)[] points) =>
        [.. points.Select(p => new[] { Longitude(p.EastKm), Latitude(p.NorthKm) })];

    private static double Longitude(double eastKm) => Longitude0 + eastKm / (KmPerDegree * Math.Cos(Latitude0 * Math.PI / 180));

    private static double Latitude(double northKm) => Latitude0 + northKm / KmPerDegree;

    /// <summary>Inside one of the polygons: inside its outline and none of its holes (even–odd over its rings).</summary>
    private static bool Inside(double[][][][] corridor, double eastKm, double northKm)
    {
        var (x, y) = (Longitude(eastKm), Latitude(northKm));
        return corridor.Any(polygon => polygon.Count(ring => Crosses(ring, x, y)) % 2 == 1);
    }

    private static bool Crosses(double[][] ring, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var (a, b) = (ring[i], ring[j]);
            if ((a[1] > y) != (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0])
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double SignedArea(double[][] ring)
    {
        var sum = 0.0;
        for (var i = 0; i + 1 < ring.Length; i++)
        {
            sum += ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1];
        }

        return sum / 2;
    }

    /// <summary>No two edges of the closed ring cross, besides neighbours meeting at their shared point.</summary>
    private static bool IsSimple(double[][] ring)
    {
        var edges = ring.Length - 1;
        for (var i = 0; i < edges; i++)
        {
            for (var j = i + 2; j < edges; j++)
            {
                if (i == 0 && j == edges - 1)
                {
                    continue;
                }

                if (Intersect(ring[i], ring[i + 1], ring[j], ring[j + 1]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Intersect(double[] p1, double[] p2, double[] q1, double[] q2)
    {
        static double Cross(double[] o, double[] a, double[] b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
        var (d1, d2) = (Cross(q1, q2, p1), Cross(q1, q2, p2));
        var (d3, d4) = (Cross(p1, p2, q1), Cross(p1, p2, q2));
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
