using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Essence.Ranking;

/// <summary>Where OSRM put a point on its roads.</summary>
/// <param name="Location">The point on the road.</param>
/// <param name="SnapMeters">From the point asked for to the road it was snapped to.</param>
public sealed record OsrmWaypoint(LatLon Location, double SnapMeters);

/// <param name="Geometry">The full line, encoded with precision 6 (<see cref="Polyline.Decode"/>).</param>
/// <param name="Waypoints">The start, then the end.</param>
public sealed record OsrmRoute(string Geometry, double DistanceMeters, double DurationSeconds, IReadOnlyList<OsrmWaypoint> Waypoints);

/// <summary>A distance table: one row per source, one column per destination, null where there's no road.</summary>
public sealed record OsrmTable(
    double?[][] DistancesMeters,
    double?[][] DurationsSeconds,
    IReadOnlyList<OsrmWaypoint> Sources,
    IReadOnlyList<OsrmWaypoint> Destinations);

public enum OsrmFailure
{
    /// <summary>No road between the points, or none near one of them (OSRM's NoRoute and NoSegment).</summary>
    NoRoute,

    /// <summary>OSRM is down, overloaded or out of reach: an answer may come later.</summary>
    Unavailable,

    /// <summary>OSRM refused the request as it was made (TooBig, Invalid…): a bug on this side.</summary>
    Bug,
}

public sealed class OsrmException(OsrmFailure failure, string? code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public OsrmFailure Failure { get; } = failure;

    /// <summary>OSRM's <c>code</c>, when it answered with one.</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// Calls to an OSRM server (osrm-routed with Essence's car profile) for Best near you and Best on the way. Every failure is
/// an <see cref="OsrmException"/>; only cancellation by the caller throws something else. Timeouts and retries belong to
/// the <see cref="HttpClient"/>'s handlers (the API's resilience pipeline).
/// </summary>
/// <remarks>
/// Snapping comes two ways. By default a point snaps to the profile's start points, which leave out autoroutes, so that a
/// station on a service road beside one snaps to its own road; with "any road", a driver or a destination snaps to the
/// nearest road, an autoroute included. One call has one way for all its points, and OSRM 26's hints are empty, so they
/// can't carry one call's snapping into another: stations are snapped first (<see cref="SnapAsync"/>), and their points on
/// their roads then go into tables on any road, where they stay on those roads.
/// </remarks>
public sealed class OsrmClient(HttpClient http)
{
    /// <summary>Where a point snaps by default (OSRM's nearest, which takes no other snapping).</summary>
    public async Task<OsrmWaypoint> NearestAsync(LatLon point, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"nearest/v1/driving/{Format(point)}?number=1&generate_hints=false", cancellationToken);
        var waypoints = Waypoints(document.RootElement, "waypoints");
        return waypoints.Count > 0 ? waypoints[0] : throw Malformed("no waypoint");
    }

    /// <summary>The fastest route by car, with its full line. Both ends snap to any road, an autoroute included.</summary>
    public async Task<OsrmRoute> RouteAsync(LatLon from, LatLon to, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            $"route/v1/driving/{Format(from)};{Format(to)}?overview=full&geometries=polyline6&steps=false&generate_hints=false&snapping=any",
            cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
        {
            throw Malformed("no route");
        }

        var route = routes[0];
        var waypoints = Waypoints(root, "waypoints");
        if (waypoints.Count != 2)
        {
            throw Malformed("not two waypoints");
        }

        return new OsrmRoute(
            route.TryGetProperty("geometry", out var geometry) && geometry.GetString() is { } line ? line : throw Malformed("no geometry"),
            Number(route, "distance"),
            Number(route, "duration"),
            waypoints);
    }

    /// <summary>
    /// Where each point snaps by default, autoroutes left out: the stations' own roads. One table call, whose distances
    /// aren't needed.
    /// </summary>
    public async Task<IReadOnlyList<OsrmWaypoint>> SnapAsync(IReadOnlyList<LatLon> points, CancellationToken cancellationToken)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var table = await TableAsync(points, [0], [.. Enumerable.Range(0, points.Count)], anyRoad: false, cancellationToken);
        return table.Destinations;
    }

    /// <summary>
    /// The road distances and durations from each source to each destination, both given as indices into
    /// <paramref name="points"/>.
    /// </summary>
    /// <param name="anyRoad">The points snap to any road, an autoroute included; otherwise to the profile's start points.</param>
    public async Task<OsrmTable> TableAsync(
        IReadOnlyList<LatLon> points,
        IReadOnlyList<int> sources,
        IReadOnlyList<int> destinations,
        bool anyRoad,
        CancellationToken cancellationToken)
    {
        var path = new StringBuilder("table/v1/driving/")
            .AppendJoin(';', points.Select(Format))
            .Append("?annotations=distance,duration&generate_hints=false")
            .Append("&sources=").AppendJoin(';', sources)
            .Append("&destinations=").AppendJoin(';', destinations);
        if (anyRoad)
        {
            path.Append("&snapping=any");
        }

        using var document = await GetAsync(path.ToString(), cancellationToken);
        var root = document.RootElement;
        var table = new OsrmTable(
            Matrix(root, "distances"), Matrix(root, "durations"), Waypoints(root, "sources"), Waypoints(root, "destinations"));
        if (table.DistancesMeters.Length != sources.Count || table.DurationsSeconds.Length != sources.Count
            || table.DistancesMeters.Concat(table.DurationsSeconds).Any(row => row.Length != destinations.Count)
            || table.Sources.Count != sources.Count || table.Destinations.Count != destinations.Count)
        {
            throw Malformed("a table of the wrong size");
        }

        return table;
    }

    /// <summary>A GET whose answer has OSRM's code "Ok"; anything else becomes an <see cref="OsrmException"/>.</summary>
    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Refused, unreachable, timed out, or the circuit open: the handlers' exceptions differ, the outcome doesn't.
            throw new OsrmException(OsrmFailure.Unavailable, null, $"OSRM didn't answer ({ex.GetType().Name}).", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                throw new OsrmException(OsrmFailure.Unavailable, null, $"OSRM answered {(int)response.StatusCode}.");
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new OsrmException(OsrmFailure.Bug, null, $"OSRM answered {(int)response.StatusCode} without JSON.", ex);
            }

            var code = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
            if (response.StatusCode == HttpStatusCode.OK && code == "Ok")
            {
                return document;
            }

            // Every error comes as a 400 with a code; only the code tells a road problem from a bad request.
            var message = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            document.Dispose();
            var failure = code is "NoRoute" or "NoSegment" ? OsrmFailure.NoRoute : OsrmFailure.Bug;
            throw new OsrmException(failure, code, $"OSRM answered {(int)response.StatusCode} {code ?? "without a code"}: {message}");
        }
    }

    private static List<OsrmWaypoint> Waypoints(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw Malformed($"no {name}");
        }

        return [.. array.EnumerateArray().Select(w => new OsrmWaypoint(Location(w), Number(w, "distance")))];
    }

    /// <summary>OSRM's [longitude, latitude].</summary>
    private static LatLon Location(JsonElement waypoint) =>
        waypoint.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Array
            && location.GetArrayLength() == 2 && location[0].ValueKind == JsonValueKind.Number && location[1].ValueKind == JsonValueKind.Number
            ? new LatLon(location[1].GetDouble(), location[0].GetDouble())
            : throw Malformed("a waypoint without a location");

    private static double?[][] Matrix(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            throw Malformed($"no {name}");
        }

        return [.. rows.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.Array
            ? row.EnumerateArray().Select(cell => cell.ValueKind == JsonValueKind.Number ? cell.GetDouble() : (double?)null).ToArray()
            : throw Malformed($"a row of {name} that isn't one"))];
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw Malformed($"no {name}");

    private static OsrmException Malformed(string what) => new(OsrmFailure.Bug, "Ok", $"OSRM answered Ok with {what}.");

    /// <summary>OSRM takes longitude first.</summary>
    private static string Format(LatLon point) =>
        string.Create(CultureInfo.InvariantCulture, $"{point.Longitude:0.######},{point.Latitude:0.######}");
}
