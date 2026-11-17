using System.Text.Json;

namespace Essence.Data.Entities;

/// <summary>
/// A station the Régie's feed may put in the wrong place (<c>position_candidates</c>), for a person to review: two
/// independent kinds of position agree with each other, far from the feed's point, and nothing of its brand is at the
/// feed's point. Found by the ingest once a day; the table is replaced as a whole each time. An approved one goes into
/// <c>curated-positions.json</c> (<see cref="PositionCorrections"/>): nothing here moves a station by itself.
/// </summary>
public sealed class PositionCandidate
{
    /// <summary>An OpenStreetMap fuel element of the station's brand, or of none, at its house number and postal code.</summary>
    public const string FromOsm = "osm";

    /// <summary>A brand's locator: the store with the number in the station's name, or one of its brand at its house number and postal code.</summary>
    public const string FromLocator = "locator";

    /// <summary>Photon's answer for the station's address: a place of OpenStreetMap's at it (an address, a building, its shop), never a fuel element.</summary>
    public const string FromPhoton = "photon";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public long StationId { get; set; }

    /// <summary>The station's natural key when it was found: a correction is keyed by it.</summary>
    public required string NaturalKey { get; set; }

    /// <summary>The feed's point when it was found.</summary>
    public double FeedLatitude { get; set; }

    public double FeedLongitude { get; set; }

    /// <summary>The point proposed: the mean of the positions that agree.</summary>
    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>From the feed's point to the proposed one.</summary>
    public int DistanceM { get; set; }

    /// <summary>The positions that agree, as JSON (<c>jsonb</c>): <see cref="PositionEvidence"/>s.</summary>
    public required string Evidence { get; set; }

    public DateTimeOffset FoundAt { get; set; }

    public Station Station { get; set; } = null!;

    /// <summary><see cref="Evidence"/> as stored.</summary>
    public static string Write(IReadOnlyList<PositionEvidence> evidence) => JsonSerializer.Serialize(evidence, Json);

    /// <summary><see cref="Evidence"/> read; empty when it can't be.</summary>
    public static IReadOnlyList<PositionEvidence> Read(string evidence)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PositionEvidence>>(evidence, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>One position of a station that agrees with another kind's (<see cref="PositionCandidate.Evidence"/>).</summary>
/// <param name="Kind"><see cref="PositionCandidate.FromOsm"/>, <see cref="PositionCandidate.FromLocator"/> or <see cref="PositionCandidate.FromPhoton"/>.</param>
/// <param name="Ref">
/// Which: the OpenStreetMap element (<c>node/5312347403</c>), the locator and its id for the store
/// (<c>ultramar/56182</c>), or the OpenStreetMap address Photon found (<c>node/1266988689</c>).
/// </param>
/// <param name="DistanceM">From the feed's point.</param>
public sealed record PositionEvidence(string Kind, string Ref, double Latitude, double Longitude, int DistanceM);
