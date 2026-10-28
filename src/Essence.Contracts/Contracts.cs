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
/// <param name="DurationMin">One way, by road; null when distances are estimated.</param>
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

/// <summary>Where to fill up on the way to a destination (<c>/trips/best</c>), counting the gas the detour burns.</summary>
/// <param name="Path">The route to draw, simplified: [longitude, latitude] pairs from the start.</param>
/// <param name="DistanceKm">The route's length, by road.</param>
/// <param name="HalfWidthKm">How far off the route stations were looked for.</param>
/// <param name="LongTrip">At least 100 km: the range counts, when it's given.</param>
/// <param name="StopBeforeKm">
/// Where along the route the range runs out, less the spare. Null when no range counts or the trip is shorter (no stop
/// needed); 0 with <paramref name="LowFuel"/>.
/// </param>
/// <param name="LowFuel">The range less the spare reaches no station: the stations within the whole range, or else the nearest.</param>
/// <param name="Stations">The stations near the route that were measured by road, in order along it.</param>
/// <param name="PickId">
/// The station recommended: the best value within the car's longest detour, or the reference when nothing saves at least
/// $0.25. Null when no station is reachable.
/// </param>
/// <param name="ReferenceId">
/// What the savings are counted against: the cheapest reachable station on the way, or with none, the smallest detour.
/// </param>
/// <param name="ReferenceOnTheWay">The reference is on the way (a stop adds at most 1 km and 2 min); otherwise it's the smallest detour.</param>
/// <param name="BiggestSavingId">The station that saves the most, past the longest detour included; null when it's the pick.</param>
public sealed record TripPlan(
    double[][] Path,
    double DistanceKm,
    double DurationMin,
    double HalfWidthKm,
    bool LongTrip,
    double? StopBeforeKm,
    bool LowFuel,
    IReadOnlyList<TripStation> Stations,
    long? PickId,
    long? ReferenceId,
    bool ReferenceOnTheWay,
    long? BiggestSavingId);

/// <param name="PriceCents">Cents per litre of the fuel asked for.</param>
/// <param name="KmAlong">How far along the route the station is.</param>
/// <param name="DistanceKm">From the start to the station, by road.</param>
/// <param name="DetourKm">What stopping there adds to the trip; 0 when it's on the way.</param>
/// <param name="CostDollars">The fill-up with the gas the detour burns.</param>
/// <param name="SavesDollars">The reference's cost minus this one's; negative when it costs more.</param>
/// <param name="MembersOnly">A Costco: it sells to members only.</param>
/// <param name="Reachable">The range gets there (always, when no range counts).</param>
/// <param name="OverDetourLimit">Its detour takes longer than the longest detour: not picked.</param>
public sealed record TripStation(
    long Id,
    string? Name,
    string? Brand,
    string Address,
    double Latitude,
    double Longitude,
    decimal PriceCents,
    double KmAlong,
    double DistanceKm,
    double DetourKm,
    double DetourMin,
    decimal CostDollars,
    decimal SavesDollars,
    bool MembersOnly,
    bool Reachable,
    bool OverDetourLimit);

/// <summary>A place to go to, from the address search (<c>/places/search</c>) or for a point (<c>/places/reverse</c>).</summary>
/// <param name="Label">What the place is called: "1000 Rue De La Gauchetière Ouest", "Rue Gaucher", "Laval", or a named place.</param>
/// <param name="Detail">Where it is: "Montréal, QC H3A 4W5" for an address, the town for a street, the address for a named place.</param>
/// <param name="Kind"><c>address</c>, <c>street</c>, <c>town</c> (or part of one), or <c>named</c> (a business, a park…).</param>
public sealed record PlaceSuggestion(string Label, string Detail, double Latitude, double Longitude, string Kind);

/// <summary>Whether trips and the address search are ready (<c>/services</c>), or how far their data is.</summary>
public sealed record DataServicesStatus(ServiceReadiness Routing, ServiceReadiness Geocoding);

/// <param name="State"><c>serving</c>, <c>building</c> (no data yet), <c>refreshing</c> (serving while building the next),
/// <c>starting</c> (loading its data), <c>waiting</c> (to try a failed build again), or <c>unknown</c> when it doesn't say.</param>
/// <param name="Ready">It answers; also true when it doesn't say (<c>unknown</c>), so nothing is promised.</param>
/// <param name="Stage">The build's stage while there's one: <c>downloading</c>, <c>extracting</c>, <c>importing</c>…</param>
/// <param name="Percent">How far the build is, 0 to 100.</param>
/// <param name="DataDate">The date of the data served, "2026-10-27".</param>
public sealed record ServiceReadiness(string State, bool Ready, string? Stage, int? Percent, string? DataDate);
