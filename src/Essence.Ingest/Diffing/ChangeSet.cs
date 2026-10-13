using Essence.Ingest.Feed;

namespace Essence.Ingest.Diffing;

/// <summary>A station as currently stored.</summary>
public sealed record StationSnapshot(
    long Id,
    string NaturalKey,
    string? Name,
    string? Brand,
    string Status,
    string Address,
    string PostalCode,
    string Region,
    double? Latitude,
    double? Longitude,
    bool IsActive);

/// <summary>A row of <c>current_prices</c>.</summary>
public sealed record PriceSnapshot(
    long StationId,
    string FuelType,
    decimal? PriceCents,
    bool IsAvailable,
    long PriceHistoryId);

public sealed record FieldChange(string Field, string? OldValue, string? NewValue);

/// <summary>An existing station whose fields changed, or that came back (<see cref="Reactivated"/>).</summary>
public sealed record StationUpdate(long StationId, FeedStation Station, IReadOnlyList<FieldChange> Changes, bool Reactivated);

/// <summary>A new current price for a station and fuel.</summary>
/// <param name="StationId">Null for a station added by this import; use <paramref name="NaturalKey"/>.</param>
/// <param name="PreviousHistoryId">The open <c>price_history</c> row this replaces, if any.</param>
public sealed record PriceChange(
    string NaturalKey,
    long? StationId,
    string FuelType,
    decimal? PriceCents,
    bool IsAvailable,
    long? PreviousHistoryId);

/// <summary>A price that stops being current: the station dropped that fuel or disappeared.</summary>
public sealed record PriceClosure(long StationId, string FuelType, long PriceHistoryId);

public sealed record ChangeSet(
    int StationsTotal,
    IReadOnlyList<FeedStation> NewStations,
    IReadOnlyList<StationUpdate> UpdatedStations,
    IReadOnlyList<long> DeactivatedStationIds,
    IReadOnlyList<PriceChange> PriceChanges,
    IReadOnlyList<PriceClosure> ClosedPrices,
    IReadOnlyList<string> Warnings)
{
    public int ReactivatedCount => UpdatedStations.Count(u => u.Reactivated);

    /// <summary>New, changed and closed prices.</summary>
    public int PricesChangedCount => PriceChanges.Count + ClosedPrices.Count;
}
