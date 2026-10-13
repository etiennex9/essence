namespace Essence.Api;

public sealed record PagedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record StationSummary(
    long Id,
    string? Name,
    string? Brand,
    string Status,
    string Address,
    string PostalCode,
    string Region,
    double? Latitude,
    double? Longitude,
    bool IsActive);

/// <param name="PriceCents">Cents per litre; null when unavailable.</param>
public sealed record CurrentPriceDto(string Fuel, decimal? PriceCents, bool IsAvailable, DateTimeOffset Since);

public sealed record StationDetail(
    long Id,
    string? Name,
    string? Brand,
    string Status,
    string Address,
    string PostalCode,
    string Region,
    double? Latitude,
    double? Longitude,
    bool IsActive,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CurrentPriceDto> Prices);

public sealed record NearbyStation(
    long Id,
    string? Name,
    string? Brand,
    string Address,
    string PostalCode,
    string Region,
    double Latitude,
    double Longitude,
    double DistanceKm,
    IReadOnlyList<CurrentPriceDto> Prices);

public sealed record PriceListing(
    long StationId,
    string? StationName,
    string? Brand,
    string Address,
    string Region,
    double? Latitude,
    double? Longitude,
    string Fuel,
    decimal? PriceCents,
    bool IsAvailable,
    DateTimeOffset Since);

/// <param name="ValidTo">Null while this is the current price.</param>
public sealed record PriceHistoryDto(string Fuel, decimal? PriceCents, bool IsAvailable, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

public sealed record ImportDto(
    long Id,
    string Status,
    string Source,
    DateTimeOffset GeneratedAt,
    DateTimeOffset FetchedAt,
    double? DelaySeconds,
    int StationsTotal,
    int StationsAdded,
    int StationsUpdated,
    int StationsDeactivated,
    int PricesChanged,
    int Warnings,
    string Sha256,
    string? Error);
