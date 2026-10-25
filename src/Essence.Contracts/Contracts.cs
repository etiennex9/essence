namespace Essence.Contracts;

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

/// <summary>Where to fill up around a point (<c>/stations/best</c>), counting the gas burned getting there and back.</summary>
/// <param name="Pick">
/// The station recommended: the best value within the car's longest detour, or the closest when nothing else saves at least
/// $0.25. Null when no station has a price for the fuel.
/// </param>
/// <param name="ReferenceId">What the savings are counted against: the closest station with a price.</param>
/// <param name="Stations">The stations worth showing, lowest cost first: the pick, the closest, the cheapest and a few that save.</param>
/// <param name="RadiusKm">
/// The straight-line radius the stations were found within: the one asked for, or 25 km when it was widened. Null when none
/// within 25 km has a price, and <paramref name="Stations"/> holds the closest one at any distance.
/// </param>
/// <param name="ByRoad">Distances are by road; otherwise they're estimated (straight line × 1.3).</param>
public sealed record BestNearby(
    RankedStation? Pick,
    long? ReferenceId,
    long? ClosestId,
    long? CheapestId,
    IReadOnlyList<RankedStation> Stations,
    double? RadiusKm,
    bool ByRoad);

/// <param name="PriceCents">Cents per litre of the fuel asked for.</param>
/// <param name="DistanceKm">One way, by road or estimated (<see cref="BestNearby.ByRoad"/>).</param>
/// <param name="DurationMin">One way, by road; null while distances are estimated.</param>
/// <param name="CostDollars">The fill-up with the gas burned getting there and back.</param>
/// <param name="SavesDollars">The reference's cost minus this one's; negative when it costs more.</param>
/// <param name="MembersOnly">A Costco: it sells to members only.</param>
/// <param name="OverDetourLimit">Its round trip takes longer than the closest's by more than the longest detour: not picked.</param>
/// <param name="SavingUncertain">It saves by the estimate, but not with roads twice the straight line (a river between): not picked.</param>
public sealed record RankedStation(
    long Id,
    string? Name,
    string? Brand,
    string Address,
    double Latitude,
    double Longitude,
    decimal PriceCents,
    double DistanceKm,
    double? DurationMin,
    decimal CostDollars,
    decimal SavesDollars,
    bool MembersOnly,
    bool OverDetourLimit,
    bool SavingUncertain);
