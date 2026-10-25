using System.Globalization;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class GoogleMapsTests
{
    [Fact]
    public void Directions_drive_to_the_point() =>
        Assert.Equal(
            "https://www.google.com/maps/dir/?api=1&destination=45.517092,-73.589503&travelmode=driving",
            GoogleMaps.Directions(45.517092, -73.589503));

    [Fact]
    public void Navigate_starts_the_navigation() =>
        Assert.EndsWith("&travelmode=driving&dir_action=navigate", GoogleMaps.Directions(45.5, -73.6, navigate: true));

    [Fact]
    public void Going_via_a_station_makes_it_a_stop_on_the_way() =>
        Assert.Equal(
            "https://www.google.com/maps/dir/?api=1&destination=46.8139,-71.208&waypoints=45.883,-72.4842&travelmode=driving"
            + "&dir_action=navigate",
            GoogleMaps.DirectionsVia(46.8139, -71.208, 45.883, -72.4842, navigate: true));

    [Fact]
    public void Coordinates_are_invariant_and_rounded_to_six_decimals()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
        try
        {
            Assert.Contains("destination=45.123457,-73.5&", GoogleMaps.Directions(45.1234567, -73.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
