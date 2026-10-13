namespace Essence.UI.Services;

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>What the map shows, as reported by the JS side after each move.</summary>
public sealed record Viewport(double CenterLat, double CenterLon, double West, double South, double East, double North, double Zoom)
{
    public GeoPoint Center => new(CenterLat, CenterLon);

    /// <summary>True when the point is inside the bounds grown by <paramref name="padFraction"/> of their size on every side.</summary>
    public bool Contains(double latitude, double longitude, double padFraction = 0)
    {
        var padLat = (North - South) * padFraction;
        var padLon = (East - West) * padFraction;
        return latitude >= South - padLat && latitude <= North + padLat
            && longitude >= West - padLon && longitude <= East + padLon;
    }
}

public static class Geo
{
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>Great-circle distance (haversine).</summary>
    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLon = ToRadians(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
