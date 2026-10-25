namespace Essence.Ranking.Tests;

public class SnappingTests
{
    [Fact]
    public void Stations_go_on_where_they_met_their_road_unless_that_was_too_far()
    {
        string[] stations = ["on its service road", "coordinates in a field", "at the curb"];
        OsrmWaypoint[] snapped = [new(new(45.4585, -73.8373), 24.4), new(new(45.6, -73.6), 301), new(new(45.5, -73.5), 300)];

        var onRoads = Snapping.OnTheirRoads(stations, snapped);

        Assert.Equal([("on its service road", new LatLon(45.4585, -73.8373)), ("at the curb", new LatLon(45.5, -73.5))], onRoads);
    }

    [Fact]
    public void Each_station_needs_its_waypoint() =>
        Assert.Throws<ArgumentException>(() => Snapping.OnTheirRoads(["one", "two"], [new OsrmWaypoint(new(45.5, -73.5), 1)]));
}
