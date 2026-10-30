namespace Essence.UI.Services;

/// <summary>A trip's route, as the API sends it: [longitude, latitude] pairs from the start, simplified.</summary>
public static class TripPath
{
    /// <summary>
    /// Splits the route where the range runs out, <paramref name="atKm"/> along it: the part the car reaches, the part past
    /// it (null when it reaches the end), and the point between them. The km along the drawn line are scaled to the
    /// route's <paramref name="lengthKm"/>, as the API counts its km along the full line.
    /// </summary>
    public static (double[][] Reached, double[][]? Beyond, GeoPoint? At) Split(double[][] path, double lengthKm, double? atKm)
    {
        if (atKm is not { } km || path.Length < 2 || km >= lengthKm)
        {
            return (path, null, null);
        }

        var cumulative = new double[path.Length];
        for (var i = 1; i < path.Length; i++)
        {
            cumulative[i] = cumulative[i - 1] + Geo.DistanceKm(Point(path[i - 1]), Point(path[i]));
        }

        var target = Math.Max(0, km) * (cumulative[^1] / lengthKm);
        var found = Array.FindIndex(cumulative, c => c > target);
        var after = found < 0 ? path.Length - 1 : Math.Max(found, 1);
        var before = after - 1;
        var span = cumulative[after] - cumulative[before];
        var t = span > 0 ? Math.Clamp((target - cumulative[before]) / span, 0, 1) : 0;
        double[] at =
        [
            path[before][0] + t * (path[after][0] - path[before][0]),
            path[before][1] + t * (path[after][1] - path[before][1]),
        ];
        return ([.. path[..after], at], [at, .. path[after..]], Point(at));
    }

    private static GeoPoint Point(double[] lonLat) => new(lonLat[1], lonLat[0]);
}
