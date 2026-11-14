using System.Globalization;
using System.Text.Json;

namespace Essence.Data;

/// <summary>
/// A reviewed correction of where the Régie's feed puts a station, from <c>curated-positions.json</c>: what the map, the
/// directions, Best near you and Best on the way and the hours' matching use instead of the feed's point, which stays in
/// the database as it is.
/// </summary>
/// <param name="NaturalKey">
/// The station's (<see cref="StationKey.Create"/>): a station whose address or postal code changes gets another key, and
/// the correction no longer applies to it.
/// </param>
/// <param name="Latitude">Where the station is.</param>
/// <param name="Longitude">Where the station is.</param>
/// <param name="FeedLatitude">Where the feed put it when the correction was reviewed.</param>
/// <param name="FeedLongitude">Where the feed put it when the correction was reviewed.</param>
/// <param name="Reviewed">The day it was reviewed.</param>
/// <param name="Note">The evidence, short: which independent positions agree, and how far from the feed's point.</param>
public sealed record PositionCorrection(
    string NaturalKey,
    double Latitude,
    double Longitude,
    double FeedLatitude,
    double FeedLongitude,
    DateOnly Reviewed,
    string Note)
{
    /// <summary>
    /// Whether it applies to the station while the feed puts it at this point: still the one reviewed (within
    /// <see cref="PositionCorrections.FeedToleranceM"/>), and not yet the corrected one (more than
    /// <see cref="PositionCorrections.FixedWithinM"/> from it). Otherwise it has expired: the Régie fixed or changed its
    /// point, and the feed's is used again.
    /// </summary>
    public bool AppliesTo(double? feedLatitude, double? feedLongitude) =>
        feedLatitude is { } latitude && feedLongitude is { } longitude
        && PositionCorrections.DistanceM(latitude, longitude, FeedLatitude, FeedLongitude) <= PositionCorrections.FeedToleranceM
        && PositionCorrections.DistanceM(latitude, longitude, Latitude, Longitude) > PositionCorrections.FixedWithinM;
}

/// <summary>What became of a correction (<see cref="PositionCorrections.StateOf"/>).</summary>
public enum PositionCorrectionState
{
    /// <summary>The station's point is the corrected one.</summary>
    Applied,

    /// <summary>The feed's point moved since the review: the Régie fixed it, or changed it. The feed's is used again.</summary>
    Expired,

    /// <summary>No active station has the natural key: it left the feed, or its address or postal code changed.</summary>
    StationGone,
}

/// <summary>
/// The stations' reviewed position corrections, written down by hand in <c>curated-positions.json</c> and shipped in this
/// assembly, so the API and the ingest read the same ones and a new image brings new ones. Entries come from the ingest's
/// candidates (<c>/stations/positions</c>), each approved by a person: a single source is too often wrong to move a point
/// by itself. An entry that can't be read is left out with a warning, the others still apply.
/// </summary>
public sealed class PositionCorrections
{
    /// <summary>How far the feed's point may move from the one reviewed and the correction still apply.</summary>
    public const double FeedToleranceM = 50;

    /// <summary>A feed point this close to the corrected one is the Régie's fix: the correction no longer applies.</summary>
    public const double FixedWithinM = 50;

    private const string ResourceName = "Essence.Data.curated-positions.json";
    private const double EarthRadiusM = 6_371_008.8;

    /// <summary>Québec, roughly: a point outside is a typo (latitude and longitude swapped, a sign lost).</summary>
    private const double South = 44.5, North = 63, West = -80, East = -56.5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<PositionCorrections> EmbeddedFile = new(() => Parse(EmbeddedBytes()));

    private readonly Dictionary<string, PositionCorrection> _byKey;

    private PositionCorrections(List<PositionCorrection> all, List<string> warnings)
    {
        All = all;
        Warnings = warnings;
        _byKey = all.ToDictionary(c => c.NaturalKey, StringComparer.Ordinal);
    }

    /// <summary>None: what a file that can't be read leaves.</summary>
    public static PositionCorrections None { get; } = new([], []);

    /// <summary>The file shipped in the assembly, read once.</summary>
    public static PositionCorrections Embedded => EmbeddedFile.Value;

    /// <summary>The corrections read, in the file's order.</summary>
    public IReadOnlyList<PositionCorrection> All { get; }

    /// <summary>Why each entry left out was, in English, for the logs; the whole file when it isn't JSON.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The file as shipped.</summary>
    public static byte[] EmbeddedBytes()
    {
        using var stream = typeof(PositionCorrections).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Reads a corrections file: <c>{"corrections": [{"naturalKey", "latitude", "longitude", "feedLatitude",
    /// "feedLongitude", "reviewed": "2026-11-14", "note"}]}</c>. Never throws: an entry that can't be read (a field
    /// missing, a point outside Québec, a corrected point that the tolerance would expire at once, a natural key already
    /// given) is left out, and a file that isn't JSON leaves none, each with a warning.
    /// </summary>
    public static PositionCorrections Parse(byte[] raw)
    {
        CorrectionsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CorrectionsFile>(raw, Json);
        }
        catch (JsonException ex)
        {
            return new PositionCorrections([], [$"The position corrections aren't valid JSON ({ex.Message}): none applies."]);
        }

        var corrections = new List<PositionCorrection>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in file?.Corrections ?? [])
        {
            index++;
            var what = $"The position correction {index} ({entry?.NaturalKey ?? "no natural key"})";
            if (Read(entry, what, warnings) is not { } correction)
            {
                continue;
            }

            if (!seen.Add(correction.NaturalKey))
            {
                warnings.Add($"{what} is the second for that station; left out.");
                continue;
            }

            corrections.Add(correction);
        }

        return new PositionCorrections(corrections, warnings);
    }

    /// <summary>The station's correction, applying or not; null when it has none.</summary>
    public PositionCorrection? Find(string naturalKey) => _byKey.GetValueOrDefault(naturalKey);

    /// <summary>The station's correction when it applies to the feed's point now (<see cref="PositionCorrection.AppliesTo"/>); else null.</summary>
    public PositionCorrection? AppliedTo(string naturalKey, double? feedLatitude, double? feedLongitude) =>
        Find(naturalKey) is { } correction && correction.AppliesTo(feedLatitude, feedLongitude) ? correction : null;

    /// <summary>Where to put the station: the corrected point when its correction applies, else the feed's.</summary>
    public (double? Latitude, double? Longitude) PointOf(string naturalKey, double? feedLatitude, double? feedLongitude) =>
        AppliedTo(naturalKey, feedLatitude, feedLongitude) is { } correction
            ? (correction.Latitude, correction.Longitude)
            : (feedLatitude, feedLongitude);

    /// <param name="station">The active station with the correction's natural key, and its feed point; null when there's none.</param>
    public static PositionCorrectionState StateOf(PositionCorrection correction, (double? Latitude, double? Longitude)? station) =>
        station is not { } point ? PositionCorrectionState.StationGone
        : correction.AppliesTo(point.Latitude, point.Longitude) ? PositionCorrectionState.Applied
        : PositionCorrectionState.Expired;

    /// <summary>The great-circle distance, in metres.</summary>
    public static double DistanceM(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var phi1 = double.DegreesToRadians(latitude1);
        var phi2 = double.DegreesToRadians(latitude2);
        var halfDPhi = (phi2 - phi1) / 2;
        var halfDLambda = double.DegreesToRadians(longitude2 - longitude1) / 2;
        var h = Math.Sin(halfDPhi) * Math.Sin(halfDPhi) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(halfDLambda) * Math.Sin(halfDLambda);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static PositionCorrection? Read(CorrectionEntry? entry, string what, List<string> warnings)
    {
        if (entry is null)
        {
            warnings.Add($"{what} is empty; left out.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(entry.NaturalKey) || !entry.NaturalKey.Contains('|'))
        {
            warnings.Add($"{what} has no natural key (\"address|POSTCODE\", as in the stations' natural_key); left out.");
            return null;
        }

        if (entry is not { Latitude: { } latitude, Longitude: { } longitude, FeedLatitude: { } feedLatitude, FeedLongitude: { } feedLongitude }
            || !InQuebec(latitude, longitude) || !InQuebec(feedLatitude, feedLongitude))
        {
            warnings.Add($"{what} needs latitude, longitude, feedLatitude and feedLongitude, in degrees, in Québec; left out.");
            return null;
        }

        if (!DateOnly.TryParseExact(entry.Reviewed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var reviewed))
        {
            warnings.Add($"{what} needs the day it was reviewed (\"reviewed\": \"YYYY-MM-DD\"); left out.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(entry.Note))
        {
            warnings.Add($"{what} needs a note saying what agrees where; left out.");
            return null;
        }

        var correction = new PositionCorrection(entry.NaturalKey, latitude, longitude, feedLatitude, feedLongitude, reviewed, entry.Note.Trim());
        if (!correction.AppliesTo(feedLatitude, feedLongitude))
        {
            warnings.Add($"{what} moves the station {FixedWithinM:0} m or less: it would never apply; left out.");
            return null;
        }

        return correction;
    }

    private static bool InQuebec(double latitude, double longitude) =>
        latitude is >= South and <= North && longitude is >= West and <= East;

    private sealed record CorrectionsFile(List<CorrectionEntry?>? Corrections);

    private sealed record CorrectionEntry(
        string? NaturalKey, double? Latitude, double? Longitude, double? FeedLatitude, double? FeedLongitude, string? Reviewed, string? Note);
}
