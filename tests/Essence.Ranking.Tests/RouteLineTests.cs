namespace Essence.Ranking.Tests;

public class RouteLineTests
{
    private const double KmPerDegree = 111.195;

    /// <summary>100 km due north from 45° N, a point every km.</summary>
    private static readonly LatLon[] North = [.. Enumerable.Range(0, 101).Select(i => new LatLon(45 + i / KmPerDegree, -73))];

    /// <summary>A point <paramref name="eastKm"/> east of the route, <paramref name="northKm"/> along it.</summary>
    private static LatLon Beside(double northKm, double eastKm) =>
        new(45 + northKm / KmPerDegree, -73 + eastKm / (KmPerDegree * Math.Cos((45 + northKm / KmPerDegree) * Math.PI / 180)));

    [Fact]
    public void A_station_beside_the_route_is_located_off_it_and_along_it()
    {
        var line = new RouteLine(North);

        var position = line.Locate(Beside(northKm: 50.4, eastKm: 2), withinKm: 5)!.Value;

        Assert.Equal(2, position.OffKm, 1);
        Assert.Equal(50.4, position.KmAlong, 1);
    }

    [Fact]
    public void A_station_beyond_the_half_width_is_not_on_the_way()
    {
        var line = new RouteLine(North);

        Assert.Null(line.Locate(Beside(50, 5.2), withinKm: 5));
        Assert.Null(line.Locate(Beside(-6, 0), withinKm: 5));
        Assert.NotNull(line.Locate(Beside(-4, 0), withinKm: 5));
    }

    [Fact]
    public void Km_along_are_scaled_to_the_length_osrm_counts()
    {
        var line = new RouteLine(North, lengthKm: 110);

        Assert.Equal(110, line.LengthKm, 6);
        Assert.Equal(55, line.Locate(Beside(50, 1), 5)!.Value.KmAlong, 1);
    }

    [Fact]
    public void The_straight_line_is_simplified_to_its_ends_but_km_along_follow_the_full_line()
    {
        // A zigzag 20 m to the east every 100 m: the simplified line is straight, the full line a little longer.
        var zigzag = Enumerable.Range(0, 201)
            .Select(i => Beside(i * 0.1, i % 2 == 0 ? 0 : 0.02))
            .ToList();

        var line = new RouteLine(zigzag);

        Assert.Equal(2, line.Simplified.Count);
        Assert.True(line.LengthKm > 20.1);
        Assert.Equal(line.LengthKm, line.Locate(Beside(20, 0.5), 1)!.Value.KmAlong, 6);
    }

    [Fact]
    public void A_route_doubling_back_locates_a_station_on_its_nearer_leg()
    {
        // 10 km north, then back south 1 km east of the way up.
        LatLon[] there = [Beside(0, 0), Beside(10, 0), Beside(10, 1), Beside(0, 1)];
        var line = new RouteLine(there);

        var position = line.Locate(Beside(5, 0.9), 2)!.Value;

        Assert.Equal(0.1, position.OffKm, 1);
        Assert.Equal(16, position.KmAlong, 1);
    }

    [Fact]
    public void A_route_needs_two_points() =>
        Assert.Throws<ArgumentException>(() => new RouteLine([new(45, -73)]));
}
