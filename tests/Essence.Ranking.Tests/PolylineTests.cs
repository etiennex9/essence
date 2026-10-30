using System.Text;

namespace Essence.Ranking.Tests;

public class PolylineTests
{
    private const double KmPerDegree = 111.195;

    [Fact]
    public void Googles_example_decodes_at_precision_5()
    {
        var points = Polyline.Decode("_p~iF~ps|U_ulLnnqC_mqNvxq`@", precision: 5);

        Assert.Equal([new(38.5, -120.2), new(40.7, -120.95), new(43.252, -126.453)], points);
    }

    [Fact]
    public void Osrm_polyline6_decodes_to_its_points()
    {
        LatLon[] route = [new(45.501689, -73.567256), new(45.508888, -73.554167), new(46.813878, -71.207981)];

        Assert.Equal(route, Polyline.Decode(Encode(route)));
    }

    [Theory]
    [InlineData("_p~iF~ps|U_")] // ends in the middle of a value
    [InlineData("_p~iF~ps|U ")] // a space is below the encoding's range
    public void A_broken_polyline_is_a_format_error(string encoded) =>
        Assert.Throws<FormatException>(() => Polyline.Decode(encoded, precision: 5));

    [Fact]
    public void Cumulative_km_add_up_along_the_line()
    {
        LatLon[] line = [new(45, -73), new(45.1, -73), new(45.2, -73)];

        var km = Polyline.CumulativeKm(line);

        Assert.Equal(0, km[0]);
        Assert.Equal(11.12, km[1], 2);
        Assert.Equal(22.24, km[2], 2);
    }

    [Fact]
    public void Simplifying_drops_wiggles_within_the_tolerance_and_keeps_corners()
    {
        // North for 10 km, wavering 10 m either side every 100 m, then a corner and 5 km east.
        var line = new List<LatLon>();
        for (var i = 0; i <= 100; i++)
        {
            line.Add(new LatLon(45 + i * 0.1 / KmPerDegree, -73 + (i % 2 == 0 ? 0.0001275 : -0.0001275)));
        }

        var corner = line.Count - 1;
        line.Add(new LatLon(line[^1].Latitude, -72.94));

        var kept = Polyline.Simplify(line, toleranceMeters: 25);

        Assert.Equal([0, corner, line.Count - 1], kept);
    }

    [Fact]
    public void A_line_of_two_points_is_kept_whole() =>
        Assert.Equal([0, 1], Polyline.Simplify([new(45, -73), new(46, -72)], 25));

    /// <summary>Google's encoding at precision 6, as OSRM writes it.</summary>
    internal static string Encode(IEnumerable<LatLon> points)
    {
        var text = new StringBuilder();
        long latitude = 0, longitude = 0;
        foreach (var point in points)
        {
            var (lat, lon) = ((long)Math.Round(point.Latitude * 1e6), (long)Math.Round(point.Longitude * 1e6));
            Write(text, lat - latitude);
            Write(text, lon - longitude);
            (latitude, longitude) = (lat, lon);
        }

        return text.ToString();
    }

    private static void Write(StringBuilder text, long value)
    {
        var bits = value < 0 ? ~(value << 1) : value << 1;
        while (bits >= 0x20)
        {
            text.Append((char)((0x20 | (bits & 0x1f)) + 63));
            bits >>= 5;
        }

        text.Append((char)(bits + 63));
    }
}
