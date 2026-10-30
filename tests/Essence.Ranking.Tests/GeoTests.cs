namespace Essence.Ranking.Tests;

public class GeoTests
{
    [Fact]
    public void Montreal_to_quebec_city_is_about_233_km() =>
        Assert.InRange(Geo.DistanceKm(45.5017, -73.5673, 46.8139, -71.2080), 231, 235);

    [Fact]
    public void A_point_is_at_no_distance_from_itself() =>
        Assert.Equal(0, Geo.DistanceKm(45.5, -73.6, 45.5, -73.6));
}
