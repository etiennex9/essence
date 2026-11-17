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

    [Fact]
    public void A_branded_station_is_looked_up_by_its_brand_and_address() =>
        Assert.Equal(
            "https://www.google.com/maps/search/?api=1&query=Petro-Canada%2C%2056%20du%20Cur%C3%A9-Labelle%2C%20Sainte-Th%C3%A9r%C3%A8se%20J7E%202X8",
            GoogleMaps.Search("Petro-Canada", "9358-1452 Québec inc.", "56 du Curé-Labelle , Sainte-Thérèse", "J7E 2X8"));

    [Theory]
    [InlineData("Indépendant", "Station du Village", "Station du Village, 627 boul. Léry, Léry J6N 1B2")]
    [InlineData(null, "Station du Village", "Station du Village, 627 boul. Léry, Léry J6N 1B2")]
    [InlineData("Aucun", "9214-4609 Québec inc.", "627 boul. Léry, Léry J6N 1B2")]
    [InlineData(null, "9214-4609 QUEBEC INC", "627 boul. Léry, Léry J6N 1B2")]
    [InlineData(null, " ", "627 boul. Léry, Léry J6N 1B2")]
    public void An_independent_is_looked_up_by_its_name_unless_it_s_only_a_numbered_company_s(string? brand, string? name, string query) =>
        Assert.Equal(query, Uri.UnescapeDataString(GoogleMaps.Search(brand, name, "627 boul.  Léry, Léry", "J6N 1B2").Split("query=")[1]));

    [Fact]
    public void The_query_is_escaped_whole() =>
        Assert.Equal(
            "https://www.google.com/maps/search/?api=1&query=Esso%2C%20166%20rue%20Turgeon%20%26%20boul.%20Labelle",
            GoogleMaps.Search("Esso", null, "166 rue Turgeon & boul. Labelle", null));
}
