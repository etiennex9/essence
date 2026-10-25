namespace Essence.Ranking;

/// <summary>Stations on their roads, before they're measured by road.</summary>
public static class Snapping
{
    /// <summary>A station that snaps farther than this from its road is left out by road: its coordinates are off.</summary>
    public const double MaxStationMeters = 300;

    /// <summary>
    /// The stations that snap within <see cref="MaxStationMeters"/> of their road, in order, each with the point where it
    /// met its road (<see cref="OsrmClient.SnapAsync"/>, one waypoint per station).
    /// </summary>
    public static List<(T Station, LatLon OnRoad)> OnTheirRoads<T>(IReadOnlyList<T> stations, IReadOnlyList<OsrmWaypoint> snapped)
    {
        if (snapped.Count != stations.Count)
        {
            throw new ArgumentException("One waypoint per station.", nameof(snapped));
        }

        return [.. stations.Zip(snapped).Where(s => s.Second.SnapMeters <= MaxStationMeters).Select(s => (s.First, s.Second.Location))];
    }
}
