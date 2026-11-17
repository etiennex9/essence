namespace Essence.Hours.Tests;

public class QuebecTimeTests
{
    [Theory]
    [InlineData(47.38, -61.86, "Atlantic")] // Cap-aux-Meules
    [InlineData(47.24, -61.94, "Atlantic")] // Havre-Aubert
    [InlineData(47.62, -61.51, "Atlantic")] // Grosse-Île
    [InlineData(50.18, -61.27, "LowerNorthShore")] // Kegaska
    [InlineData(51.42, -57.13, "LowerNorthShore")] // Blanc-Sablon
    [InlineData(50.19, -61.81, "Eastern")] // Natashquan
    [InlineData(50.22, -62.08, "Eastern")] // Aguanish
    [InlineData(50.29, -62.80, "Eastern")] // Baie-Johan-Beetz
    [InlineData(49.82, -64.35, "Eastern")] // Port-Menier, on Anticosti
    [InlineData(48.83, -64.48, "Eastern")] // Gaspé
    [InlineData(45.50, -73.57, "Eastern")] // Montréal
    public void A_station_is_in_its_town_s_zone(double latitude, double longitude, string zone) =>
        Assert.Same(Named(zone), QuebecTime.ZoneFor(latitude, longitude));

    [Theory]
    [InlineData(null, null)]
    [InlineData(47.38, null)]
    [InlineData(null, -61.86)]
    public void Without_coordinates_it_s_eastern(double? latitude, double? longitude) =>
        Assert.Same(QuebecTime.Eastern, QuebecTime.ZoneFor(latitude, longitude));

    [Theory]
    [InlineData("Eastern", -5, -4)]
    [InlineData("Atlantic", -4, -3)]
    [InlineData("LowerNorthShore", -4, -4)]
    public void Each_zone_s_winter_and_summer(string zone, int winter, int summer)
    {
        Assert.Equal(TimeSpan.FromHours(winter), Named(zone).GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(TimeSpan.FromHours(summer), Named(zone).GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("America/Toronto", -5, true)]
    [InlineData("America/Halifax", -4, true)]
    [InlineData("America/Blanc-Sablon", -4, false)]
    public void Without_the_system_s_zones_canada_s_rule_keeps_the_same_time_to_2035(string id, int standardHours, bool daylightTime)
    {
        var system = SystemZone(id);
        Assert.SkipWhen(system is null, $"This system has no {id} to compare with.");
        var fallback = QuebecTime.Canadian(id, TimeSpan.FromHours(standardHours), daylightTime);

        var end = new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); at < end; at = at.AddHours(1))
        {
            var (expected, actual) = (system!.GetUtcOffset(at), fallback.GetUtcOffset(at));
            if (expected != actual)
            {
                Assert.Fail($"{id} at {at:u}: {expected} on this system, {actual} by Canada's rule.");
            }
        }
    }

    private static TimeZoneInfo Named(string zone) => zone switch
    {
        "Eastern" => QuebecTime.Eastern,
        "Atlantic" => QuebecTime.Atlantic,
        "LowerNorthShore" => QuebecTime.LowerNorthShore,
        _ => throw new ArgumentOutOfRangeException(nameof(zone)),
    };

    private static TimeZoneInfo? SystemZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
