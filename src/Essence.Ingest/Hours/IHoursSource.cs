namespace Essence.Ingest.Hours;

/// <summary>
/// Where stations' opening hours come from: a brand's locator, OpenStreetMap, or rules written by hand. Fetching and
/// reading are apart, so the last download kept on disk (<see cref="HoursCache"/>) can be read and matched again
/// without the network.
/// </summary>
public interface IHoursSource
{
    /// <summary>Lowercase, as stored in <c>station_hours.source</c> and <c>hours_imports.source</c>: <c>costco</c>.</summary>
    string Name { get; }

    bool Enabled { get; }

    /// <summary>
    /// The source's rules come with the image, written by hand: nothing is downloaded. It runs on each start, whatever the
    /// schedule says, so a new image's rules are in at once, and the gate takes it as the owner wrote it, fewer rules or
    /// none included (<see cref="HoursGate.Check(IHoursSource, HoursParse, int, int?, double)"/>).
    /// </summary>
    bool ShippedInImage { get; }

    /// <summary>
    /// How far from a station a record with a point may be and still be that station's (<see cref="HoursMatcher"/>); a
    /// Costco record, whatever the source, within <see cref="HoursMatcher.CostcoReachM"/>. Further, up to
    /// <see cref="HoursMatcher.AddressReachM"/>, only at the station's house number and postal code.
    /// </summary>
    double MatchRadiusM { get; }

    /// <summary>
    /// How far from a station left without a record a record of another brand, or none, may be and still be that
    /// station's, its brand out of date (<see cref="HoursMatcher"/>): without its hours, so the station knows which element
    /// to edit. Null for a source whose brands are its own: a brand's locator lists only its stations.
    /// </summary>
    double? OtherBrandReachM { get; }

    /// <param name="cachedVersion">The version of the download kept on disk: the source may answer <see cref="HoursFetch.NotModified"/> when it's the same.</param>
    /// <exception cref="HttpRequestException">The source doesn't answer, or answers with an error.</exception>
    /// <exception cref="HoursFormatException">The answer isn't what the source sends.</exception>
    Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a download: the records whose hours could be read for sure, and a warning for each one left out. Never
    /// guesses: hours it doesn't understand leave the record out, and the station's other sources serve.
    /// </summary>
    /// <exception cref="HoursFormatException">The download isn't what the source sends.</exception>
    HoursParse Parse(byte[] raw);
}

public abstract record HoursFetch
{
    private HoursFetch()
    {
    }

    /// <summary>The same as the download kept on disk.</summary>
    public sealed record NotModified : HoursFetch;

    /// <param name="Raw">What <see cref="IHoursSource.Parse"/> reads, and what's kept on disk: only the fields read, never people's names or phones.</param>
    /// <param name="Version">Lowercase hex SHA-256 of <paramref name="Raw"/>.</param>
    public sealed record Fetched(byte[] Raw, string Version) : HoursFetch;
}

/// <param name="Warnings">Why each record left out was, for the logs.</param>
public sealed record HoursParse(IReadOnlyList<SourceRecord> Records, IReadOnlyList<string> Warnings);

/// <summary>
/// One place's hours, as a source gives them. A record finds its station (<see cref="HoursMatcher"/>) by
/// <see cref="NaturalKey"/> when it has one, else by <see cref="StoreNumber"/>, else by its point within the source's
/// radius among stations of <see cref="BrandFamily"/> (or further at the same address), else it's a rule for every
/// station of <see cref="BrandFamily"/>. A record for one place has a natural key, a store number or a point: without
/// any, it would be its brand's rule.
/// </summary>
/// <param name="Ref">The source's id for the place, stored as <c>source_ref</c>: Costco's warehouse number, <c>node/123</c>…</param>
/// <param name="BrandFamily">The brand, as <see cref="Hours.BrandFamily.Of"/> folds it: <c>costco</c>.</param>
/// <param name="HouseNumber">The address's number, which confirms a match by distance (<see cref="HoursMatcher.HouseNumber"/>).</param>
/// <param name="Postcode">The postal code folded as <see cref="Data.StationKey.NormalizePostalCode"/> does (<c>H7P5P4</c>); it confirms a match too.</param>
/// <param name="Rule">The canonical opening_hours (<see cref="Essence.Hours.OpeningHours.ToRule"/>), or null when the source has the place but not its hours.</param>
/// <param name="Link">Where a person can check the hours.</param>
/// <param name="CheckedAt">When the source says the hours were last checked, if it says.</param>
/// <param name="TemporarilyClosed">The source says the place is closed for now.</param>
public sealed record SourceRecord(
    string Ref,
    string? BrandFamily,
    double? Latitude,
    double? Longitude,
    string? HouseNumber,
    string? Postcode,
    string? Rule,
    string? Link,
    DateTimeOffset? CheckedAt,
    bool TemporarilyClosed)
{
    /// <summary>A station's natural key (<see cref="Data.StationKey.Create"/>), for a rule written for one station.</summary>
    public string? NaturalKey { get; init; }

    /// <summary>
    /// Couche-Tard's number for the store (<c>140</c> for its site 6000140), which the feed's names carry
    /// (<see cref="HoursMatcher.StoreNumber"/>): the record matches the station named after it, wherever it is, and no
    /// other.
    /// </summary>
    public int? StoreNumber { get; init; }
}

/// <summary>The feed's stations, for a source that asks only about the places it could match (Shell's details).</summary>
public interface IHoursStations
{
    Task<StationSet> LoadStationsAsync(CancellationToken cancellationToken);
}

public static class HoursVersion
{
    /// <summary>A download's version: the lowercase hex SHA-256 of its bytes.</summary>
    public static string Of(byte[] raw) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw));
}

/// <summary>A source's answer, or a download kept on disk, isn't what the source sends.</summary>
public sealed class HoursFormatException(string message, Exception? innerException = null) : Exception(message, innerException);
