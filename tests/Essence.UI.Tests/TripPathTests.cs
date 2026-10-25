using Essence.UI.Services;

namespace Essence.UI.Tests;

public class TripPathTests
{
    /// <summary>About 33 km due north, in three legs.</summary>
    private static readonly double[][] North = [[-73, 45], [-73, 45.1], [-73, 45.2], [-73, 45.3]];

    [Fact]
    public void The_route_splits_where_the_range_runs_out()
    {
        var (reached, beyond, at) = TripPath.Split(North, lengthKm: 33.36, atKm: 16.68);

        Assert.Equal(45.15, at!.Value.Latitude, 3);
        Assert.Equal(-73, at.Value.Longitude, 6);
        Assert.Equal([[-73, 45], [-73, 45.1], [at.Value.Longitude, at.Value.Latitude]], reached);
        Assert.Equal([[at.Value.Longitude, at.Value.Latitude], [-73, 45.2], [-73, 45.3]], beyond!);
    }

    [Fact]
    public void The_km_are_scaled_to_the_routes_length()
    {
        // The road is twice the drawn line: km 16.68 of 66.72 is a quarter of the way.
        var (_, _, at) = TripPath.Split(North, lengthKm: 66.72, atKm: 16.68);

        Assert.Equal(45.075, at!.Value.Latitude, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(40.0)]
    public void A_range_that_reaches_the_end_leaves_the_route_whole(double? atKm)
    {
        var (reached, beyond, at) = TripPath.Split(North, 33.36, atKm);

        Assert.Same(North, reached);
        Assert.Null(beyond);
        Assert.Null(at);
    }

    [Fact]
    public void Low_fuel_splits_at_the_start()
    {
        var (reached, beyond, at) = TripPath.Split(North, 33.36, atKm: 0);

        Assert.Equal(45, at!.Value.Latitude, 6);
        Assert.Equal(2, reached.Length);
        Assert.Equal(North.Length, beyond!.Length);
    }
}
