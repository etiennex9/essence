namespace Essence.Ranking.Tests;

public class SearchAreaTests
{
    [Fact]
    public void Enough_stations_within_the_radius()
    {
        double[] distances = [1, 2, 3, 4, 5, 9.9, 10, 12];

        var (stations, radiusKm) = SearchArea.Select(distances, km => km, 10);

        Assert.Equal([1, 2, 3, 4, 5, 9.9, 10], stations);
        Assert.Equal(10, radiusKm);
    }

    [Fact]
    public void Fewer_than_five_widens_to_25_km()
    {
        double[] distances = [1, 2, 3, 4, 18, 25, 26];

        var (stations, radiusKm) = SearchArea.Select(distances, km => km, 10);

        Assert.Equal([1, 2, 3, 4, 18, 25], stations);
        Assert.Equal(25, radiusKm);
    }

    [Fact]
    public void Still_fewer_than_five_within_25_km_keeps_those()
    {
        double[] distances = [3, 30];

        var (stations, radiusKm) = SearchArea.Select(distances, km => km, 10);

        Assert.Equal([3], stations);
        Assert.Equal(25, radiusKm);
    }

    [Fact]
    public void None_within_25_km_gives_the_closest_at_any_distance()
    {
        double[] distances = [80, 41, 300];

        var (stations, radiusKm) = SearchArea.Select(distances, km => km, 10);

        Assert.Equal([41], stations);
        Assert.Null(radiusKm);
    }

    [Fact]
    public void No_stations_at_all()
    {
        var (stations, radiusKm) = SearchArea.Select(Array.Empty<double>(), km => km, 10);

        Assert.Empty(stations);
        Assert.Equal(25, radiusKm);
    }
}
