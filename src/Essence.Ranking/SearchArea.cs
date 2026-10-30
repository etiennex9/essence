namespace Essence.Ranking;

/// <summary>Which stations Best near you looks at around the driver.</summary>
public static class SearchArea
{
    public const double DefaultRadiusKm = 10;
    public const double MaxRadiusKm = 25;

    /// <summary>With fewer stations than this within the radius asked for, the search widens to <see cref="MaxRadiusKm"/>.</summary>
    public const int EnoughStations = 5;

    /// <summary>
    /// The stations within <paramref name="radiusKm"/> (straight line); within <see cref="MaxRadiusKm"/> when that's fewer
    /// than <see cref="EnoughStations"/>; and with none there either, the closest at any distance.
    /// </summary>
    /// <returns>The stations and the radius they were found within: null for the closest at any distance.</returns>
    public static (List<T> Stations, double? RadiusKm) Select<T>(IReadOnlyCollection<T> stations, Func<T, double> distanceKm, double radiusKm)
    {
        var within = Within(stations, distanceKm, radiusKm);
        if (within.Count < EnoughStations && radiusKm < MaxRadiusKm)
        {
            radiusKm = MaxRadiusKm;
            within = Within(stations, distanceKm, radiusKm);
        }

        if (within.Count > 0 || stations.Count == 0)
        {
            return (within, radiusKm);
        }

        return ([stations.MinBy(distanceKm)!], null);
    }

    private static List<T> Within<T>(IEnumerable<T> stations, Func<T, double> distanceKm, double radiusKm) =>
        stations.Where(s => distanceKm(s) <= radiusKm).ToList();
}
