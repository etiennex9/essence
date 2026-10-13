using Essence.Data.Entities;

namespace Essence.Ingest.Feed;

/// <summary>A parsed and cleaned-up feed file.</summary>
/// <param name="GeneratedAt">The file's <c>metadata.generated_at</c> in UTC, truncated to microseconds (PostgreSQL's precision).</param>
/// <param name="Warnings">Data problems that were skipped, e.g. unparsable prices.</param>
public sealed record FeedDocument(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<FeedStation> Stations,
    IReadOnlyList<string> Warnings);

/// <param name="Status">Null when the file has no status (the xlsx): the stored one is kept.</param>
/// <param name="SkippedFuelTypes">Fuel types whose price couldn't be parsed: their current price is left as is, not closed.</param>
/// <param name="NotAvailableFuelTypes">
/// Fuel types shown as "N/D" in the xlsx, which means either "unavailable" or "not sold": unavailable if the station
/// currently has a price for that fuel, otherwise ignored.
/// </param>
public sealed record FeedStation(
    string NaturalKey,
    string? Name,
    string? Brand,
    string? Status,
    string Address,
    string PostalCode,
    string Region,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<FeedPrice> Prices,
    IReadOnlyList<string> SkippedFuelTypes,
    IReadOnlyList<string> NotAvailableFuelTypes);

/// <param name="PriceCents">Cents per litre, null when unavailable.</param>
public sealed record FeedPrice(string FuelType, decimal? PriceCents, bool IsAvailable);

/// <summary>Identifies the newest imported version of the feed.</summary>
public sealed record FeedVersion(DateTimeOffset GeneratedAt, string Sha256, string? ETag);

/// <summary>A new version of the feed, ready to archive and import.</summary>
/// <param name="RawBytes">What goes in the raw archive: the JSON gzipped as received (or by us), or the xlsx as downloaded.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the decompressed JSON, or of the xlsx file.</param>
public sealed record FeedCandidate(
    FeedDocument Document,
    byte[] RawBytes,
    string Sha256,
    string? ETag,
    DateTimeOffset FetchedAt,
    ImportSource Source = ImportSource.Feed)
{
    public DateTimeOffset GeneratedAt => Document.GeneratedAt;

    /// <summary>How long the file took to reach us after it was generated. Null for a backfilled xlsx, fetched late on purpose.</summary>
    public double? DelaySeconds => Source == ImportSource.Feed ? (FetchedAt - GeneratedAt).TotalSeconds : null;
}

/// <summary>The feed couldn't be decoded or doesn't have the expected shape.</summary>
public sealed class FeedFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
