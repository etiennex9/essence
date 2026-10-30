namespace Essence.Ranking.Tests;

public class QuebecAreaTests
{
    [Theory]
    [InlineData(45.5017, -73.5673)] // Montréal
    [InlineData(46.8139, -71.2080)] // Québec
    [InlineData(45.4330, -75.7050)] // Gatineau, by the Ottawa River
    [InlineData(45.0100, -72.0950)] // Stanstead, beside Derby Line
    [InlineData(45.0800, -73.3700)] // Lacolle
    [InlineData(45.6300, -74.6000)] // Grenville, across from Hawkesbury
    [InlineData(47.5500, -68.6500)] // Dégelis, near New Brunswick
    [InlineData(52.7880, -67.0850)] // Fermont, near Labrador City
    [InlineData(51.4260, -57.1310)] // Blanc-Sablon, at the Labrador border
    [InlineData(47.3800, -61.8600)] // Îles-de-la-Madeleine
    [InlineData(58.1000, -68.4000)] // Kuujjuaq
    public void A_point_in_Québec_is_in_it(double latitude, double longitude) =>
        Assert.True(QuebecArea.Contains(new LatLon(latitude, longitude)));

    [Theory]
    [InlineData(45.4260, -75.6950)] // Downtown Ottawa, by the river: a Québec road is under 1 km away
    [InlineData(45.4290, -75.6980)] // Ottawa's Byward Market
    [InlineData(45.0030, -72.0990)] // Derby Line, Vermont, 300 m from Stanstead
    [InlineData(44.9870, -73.4460)] // Champlain, New York, by the A-15's border crossing
    [InlineData(45.6060, -74.6050)] // Hawkesbury, Ontario
    [InlineData(47.3730, -68.3250)] // Edmundston, New Brunswick
    [InlineData(52.9450, -66.9110)] // Labrador City
    [InlineData(51.4290, -57.0640)] // L'Anse-au-Clair, Labrador
    [InlineData(43.6532, -79.3832)] // Toronto, outside the box
    [InlineData(48.8566, 2.3522)] // Paris
    public void A_point_past_the_border_isnt(double latitude, double longitude) =>
        Assert.False(QuebecArea.Contains(new LatLon(latitude, longitude)));

    [Theory]
    [InlineData("Québec", true)]
    [InlineData("Quebec", true)]
    [InlineData("QUÉBEC", true)]
    [InlineData(" quebec ", true)]
    [InlineData("Ontario", false)]
    [InlineData("Nouveau-Brunswick", false)]
    [InlineData(null, false)]
    public void Photon_names_the_province_in_either_language(string? state, bool quebec) =>
        Assert.Equal(quebec, QuebecArea.IsQuebec(state));
}
