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

/// <summary>
/// A station's opening hours, as the API chose them among its sources: Costco's own over hours written down by Essence,
/// those over Ultramar's and Shell's forecourts', then Couche-Tard's stores', then OpenStreetMap's (confirmed first), and a
/// source that's gone stale after the fresh ones.
/// </summary>
/// <param name="Rule">
/// The canonical opening_hours that Essence.Hours reads (<c>OpeningHours.Parse</c>): <c>Mo-Fr 06:00-21:30; Sa-Su
/// 06:00-20:30</c>, <c>24/7</c>. In the station's time zone.
/// </param>
/// <param name="Source">
/// Where they come from: <c>costco</c>, <c>curated</c> (written down by hand and checked by Essence), <c>circlek</c>,
/// <c>ultramar</c>, <c>shell</c> or <c>osm</c> (OpenStreetMap). Clients show a source they don't know as it is.
/// </param>
/// <param name="Trusted">
/// Sure enough to leave a station that's closed out of a recommendation: a brand's hours, or Essence's, from a source that
/// isn't stale (a success within 21 days). OpenStreetMap's, and a stale source's, are only shown.
/// </param>
/// <param name="Confirmed">The source's place is surely this station: its address, its brand's rule, or a matching postal code or house number.</param>
/// <param name="CheckedAt">When the source says the hours were last checked (or edited), if it says.</param>
/// <param name="Link">Where a person can check the hours.</param>
/// <param name="TemporarilyClosed">The source says the station is closed for now, whatever its hours.</param>
public sealed record StationHours(
    string Rule,
    string Source,
    bool Trusted,
    bool Confirmed,
    DateTimeOffset? CheckedAt,
    string? Link,
    bool TemporarilyClosed = false);

/// <param name="Hours">Its opening hours; null when no source has them (hours unknown).</param>
/// <param name="OsmElement">
/// The OpenStreetMap element the station was matched to, its hours or not: <c>node/123</c>, <c>way/456</c> or
/// <c>relation/789</c>, so the app can send a correction of its hours there. Null when none was, and from an older API.
/// </param>
/// <param name="OsmBrandDiffers">
/// That element was matched to the station by place only: on OpenStreetMap it's of another brand or none, its brand
/// likely out of date, so the app offers to set the station's brand on it too (when the station has one). False when
/// it's of the station's brand; null when there's no element, and from an older API.
/// </param>
/// <param name="OsmBrand">
/// The brand that element has on OpenStreetMap, as Essence last read it: its <c>brand</c>, else its <c>operator</c>, else
/// its <c>name</c> ("Sonic"), when some station of the feed has that brand. Null when it has none (a name of its own,
/// "Dépanneur Sonic", is no brand), when there's no element, and from an older API. An element with no <c>brand</c> tag
/// whose name is this is named after its brand: setting the station's brand renames it too.
/// </param>
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
    IReadOnlyList<CurrentPriceDto> Prices,
    StationHours? Hours = null,
    string? OsmElement = null,
    bool? OsmBrandDiffers = null,
    string? OsmBrand = null);

/// <param name="Hours">Its opening hours; null when no source has them (hours unknown).</param>
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
    IReadOnlyList<CurrentPriceDto> Prices,
    StationHours? Hours = null);

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
/// <param name="LeftOut">
/// Stations left out because their trusted hours say they'll be closed when the driver gets there, or their source that
/// they're closed for now: at most 2, only those cheaper per litre than the pick, lowest fill-up cost first. Null when none
/// was, and from an older API.
/// </param>
public sealed record BestNearby(
    RankedStation? Pick,
    long? ReferenceId,
    long? ClosestId,
    long? CheapestId,
    IReadOnlyList<RankedStation> Stations,
    double? RadiusKm,
    bool ByRoad,
    IReadOnlyList<LeftOutStation>? LeftOut = null);

/// <param name="PriceCents">Cents per litre of the fuel asked for.</param>
/// <param name="DistanceKm">One way, by road or estimated (<see cref="BestNearby.ByRoad"/>).</param>
/// <param name="DurationMin">One way, by road; null when distances are estimated.</param>
/// <param name="CostDollars">The fill-up with the gas burned getting there and back.</param>
/// <param name="SavesDollars">The reference's cost minus this one's; negative when it costs more.</param>
/// <param name="MembersOnly">A Costco: it sells to members only.</param>
/// <param name="OverDetourLimit">Its round trip takes longer than the closest's by more than the longest detour: not picked.</param>
/// <param name="SavingUncertain">It saves by the estimate, but not with roads twice the straight line (a river between): not picked.</param>
/// <param name="Hours">Its opening hours; null when no source has them, and from an older API.</param>
/// <param name="EtaMin">
/// How long getting there takes, one way: by road (<paramref name="DurationMin"/>), or estimated like the distance. Its
/// hours were checked at the time of the answer plus this. Null from an older API.
/// </param>
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
    bool SavingUncertain,
    StationHours? Hours = null,
    double? EtaMin = null);

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
/// <param name="LeftOut">
/// Stations left out because their trusted hours say they'll be closed when the car gets there, or their source that
/// they're closed for now: at most 2, only those cheaper per litre than the pick, lowest fill-up cost first. Null when none
/// was, and from an older API.
/// </param>
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
    long? BiggestSavingId,
    IReadOnlyList<LeftOutStation>? LeftOut = null);

/// <param name="PriceCents">Cents per litre of the fuel asked for.</param>
/// <param name="KmAlong">How far along the route the station is.</param>
/// <param name="DistanceKm">From the start to the station, by road.</param>
/// <param name="DetourKm">What stopping there adds to the trip; 0 when it's on the way.</param>
/// <param name="CostDollars">The fill-up with the gas the detour burns.</param>
/// <param name="SavesDollars">The reference's cost minus this one's; negative when it costs more.</param>
/// <param name="MembersOnly">A Costco: it sells to members only.</param>
/// <param name="Reachable">The range gets there (always, when no range counts).</param>
/// <param name="OverDetourLimit">Its detour takes longer than the longest detour: not picked.</param>
/// <param name="Hours">Its opening hours; null when no source has them, and from an older API.</param>
/// <param name="ToMin">From the start to the station, by road: its hours were checked at the time of the answer plus this. Null from an older API.</param>
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
    bool OverDetourLimit,
    StationHours? Hours = null,
    double? ToMin = null);

/// <summary>
/// A station Best near you or Best on the way left out (<see cref="BestNearby.LeftOut"/>, <see cref="TripPlan.LeftOut"/>):
/// its trusted hours say it'll be closed when the driver gets there, or its source that it's closed for now.
/// </summary>
/// <param name="PriceCents">Cents per litre of the fuel asked for, cheaper than the pick's.</param>
/// <param name="Hours">The hours it was left out by.</param>
/// <param name="EtaMin">How long getting there would take, one way (from the start, on a trip): it's closed at the time of the answer plus this.</param>
public sealed record LeftOutStation(
    long Id,
    string? Name,
    string? Brand,
    string Address,
    double Latitude,
    double Longitude,
    decimal PriceCents,
    StationHours Hours,
    double EtaMin);

/// <summary>A place to go to, from the address search (<c>/places/search</c>) or for a point (<c>/places/reverse</c>).</summary>
/// <param name="Label">What the place is called: "1000 Rue De La Gauchetière Ouest", "Rue Gaucher", "Laval", or a named place.</param>
/// <param name="Detail">Where it is: "Montréal, QC H3A 4W5" for an address, the town for a street, the address for a named place.</param>
/// <param name="Kind"><c>address</c>, <c>street</c>, <c>town</c> (or part of one), or <c>named</c> (a business, a park…).</param>
public sealed record PlaceSuggestion(string Label, string Detail, double Latitude, double Longitude, string Kind);

/// <summary>Whether trips and the address search are ready (<c>/services</c>), or how far their data is.</summary>
public sealed record DataServicesStatus(ServiceReadiness Routing, ServiceReadiness Geocoding);

/// <param name="State"><c>serving</c>, <c>building</c> (no data yet), <c>refreshing</c> (serving while building the next),
/// <c>starting</c> (loading its data), <c>waiting</c> (to try a failed build again, or to start its server again after it
/// stopped), or <c>unknown</c> when it doesn't say.</param>
/// <param name="Ready">It answers; also true when it doesn't say (<c>unknown</c>), so nothing is promised.</param>
/// <param name="Stage">The build's stage while there's one: <c>downloading</c>, <c>extracting</c>, <c>importing</c>…</param>
/// <param name="Percent">How far the build is, 0 to 100.</param>
/// <param name="DataDate">The date of the data served, "2026-10-27".</param>
/// <param name="LastError">What failed last (in English, for the logs and the curious), while it's still the case.</param>
/// <param name="NextAttemptAt">While <c>waiting</c>: when it tries again.</param>
public sealed record ServiceReadiness(
    string State, bool Ready, string? Stage, int? Percent, string? DataDate, string? LastError, DateTimeOffset? NextAttemptAt);

/// <summary>Where stations' opening hours come from (<c>/hours/sources</c>), and how each source's runs are going.</summary>
/// <param name="Source"><c>costco</c>, <c>curated</c>…</param>
/// <param name="LastSuccessAt">When its hours were last fetched and written, changed or not; null when never.</param>
/// <param name="LastAttemptAt">When it was last fetched, whatever came of it.</param>
/// <param name="LastStatus">What came of that: <c>succeeded</c>, <c>unchanged</c>, <c>rejected</c> (the answer looked wrong, and the last good hours kept serving) or <c>failed</c>.</param>
/// <param name="LastError">Why that failed or was rejected, in English; null when it didn't.</param>
/// <param name="ConsecutiveFailures">Failed or rejected fetches in a row since the last success.</param>
/// <param name="Stations">The stations it has hours for now.</param>
/// <param name="DataVersion">The version (SHA-256) of the download those hours come from.</param>
public sealed record HoursSourceStatus(
    string Source,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    string? LastStatus,
    string? LastError,
    int ConsecutiveFailures,
    int Stations,
    string? DataVersion);

/// <summary>
/// Whether every source the ingest reads is doing fine (<c>/health/sources</c>), for a monitor to alert on: answered with
/// 200 when they all are, 503 when one isn't.
/// </summary>
/// <param name="Status"><c>ok</c>, or <c>failing</c> when a source isn't fine or the runs can't be read.</param>
/// <param name="CheckedAt">When this was worked out.</param>
/// <param name="Sources">The feed (<c>feed</c>) first, then each hours source that has run and isn't turned off, by name.</param>
/// <param name="Reason">Null, or <c>database unreachable</c>: the runs couldn't be read, and <paramref name="Sources"/> is empty.</param>
public sealed record SourceHealthReport(
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<IngestSourceHealth> Sources,
    string? Reason = null);

/// <summary>How one source the ingest reads is doing, in <see cref="SourceHealthReport"/>.</summary>
/// <param name="Source"><c>feed</c> (the Régie's prices), or an hours source: <c>costco</c>, <c>curated</c>…</param>
/// <param name="Ok">Whether it's fine: the report is <c>failing</c> as soon as one isn't.</param>
/// <param name="Reason">
/// Null when it's fine; else <c>failing</c> (an hours source failed or was rejected that many times in a row),
/// <c>stale</c> (an hours source without a success in that many days), <c>never succeeded</c> (the feed, or an hours source
/// that failed that many times since its first run), or <c>feed stale</c> (no successful import of the feed for that many
/// minutes).
/// </param>
/// <param name="ConsecutiveFailures">
/// Failed runs in a row since the last success: for an hours source, its failed or rejected fetches; for the feed, its
/// failed imports (a download that fails isn't logged as one).
/// </param>
/// <param name="LastError">The latest of those failures' error, its first line, in English; null when it hasn't failed since its last success.</param>
/// <param name="LastSuccessAt">When it last succeeded; null when it never did.</param>
/// <param name="LastAttemptAt">When it was last tried (for the feed, its latest import), whatever came of it.</param>
public sealed record IngestSourceHealth(
    string Source,
    bool Ok,
    string? Reason,
    int ConsecutiveFailures,
    string? LastError,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt);

/// <summary>
/// What the apps need of their server's settings (<c>/client-config</c>): the Android app picks its server at run time, so
/// what differs between deployments comes from the server rather than the build.
/// </summary>
/// <param name="OsmClientId">
/// The client id of the deployment's OpenStreetMap OAuth 2 application (a public client, no secret), with which a device
/// links its own OpenStreetMap account to send its hours corrections there; null when the server has none, and editing
/// OpenStreetMap is off.
/// </param>
/// <param name="OsmAuthUrl">Where that application signs in and gets its token: <c>https://www.openstreetmap.org</c>, or OpenStreetMap's development server.</param>
/// <param name="OsmApiUrl">Where edits go: <c>https://api.openstreetmap.org</c>, or the development server's.</param>
public sealed record ClientConfig(string? OsmClientId, string OsmAuthUrl, string OsmApiUrl);
