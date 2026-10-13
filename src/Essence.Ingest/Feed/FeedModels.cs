namespace Essence.Ingest.Feed;

/// <summary>A parsed and cleaned-up feed file.</summary>
/// <param name="GeneratedAt">The file's <c>metadata.generated_at</c> in UTC, truncated to microseconds (PostgreSQL's precision).</param>
/// <param name="Warnings">Data problems that were skipped, e.g. unparsable prices.</param>
public sealed record FeedDocument(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<FeedStation> Stations,
    IReadOnlyList<string> Warnings);

/// <param name="SkippedFuelTypes">Fuel types whose price couldn't be parsed: their current price is left as is, not closed.</param>
public sealed record FeedStation(
    string NaturalKey,
    string? Name,
    string? Brand,
    string Status,
    string Address,
    string PostalCode,
    string Region,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<FeedPrice> Prices,
    IReadOnlyList<string> SkippedFuelTypes);

/// <param name="PriceCents">Cents per litre, null when unavailable.</param>
public sealed record FeedPrice(string FuelType, decimal? PriceCents, bool IsAvailable);

/// <summary>Identifies the newest imported version of the feed.</summary>
public sealed record FeedVersion(DateTimeOffset GeneratedAt, string Sha256, string? ETag);

/// <summary>A new version of the feed, ready to archive and import.</summary>
/// <param name="GzipBytes">The body as received (or gzipped by us if the server sent it uncompressed).</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the decompressed JSON.</param>
public sealed record FeedCandidate(
    FeedDocument Document,
    byte[] GzipBytes,
    string Sha256,
    string? ETag,
    DateTimeOffset FetchedAt)
{
    public DateTimeOffset GeneratedAt => Document.GeneratedAt;

    /// <summary>How long the file took to reach us after it was generated.</summary>
    public double DelaySeconds => (FetchedAt - GeneratedAt).TotalSeconds;
}

/// <summary>The feed couldn't be decoded or doesn't have the expected shape.</summary>
public sealed class FeedFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
