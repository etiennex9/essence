using System.Globalization;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Essence.Hours;
using Essence.Ranking;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>An active station with coordinates and its current prices, as <see cref="StationSnapshot"/> keeps it.</summary>
/// <param name="Latitude">Where it is: a reviewed correction's point when one applies (<see cref="Snapshot.Corrections"/>), else the feed's.</param>
/// <param name="Longitude">Where it is.</param>
/// <param name="MembersOnly">A Costco (<see cref="Brands.IsMembersOnly"/>).</param>
public sealed record SnapshotStation(
    long Id,
    string? Name,
    string? Brand,
    string Status,
    string Address,
    double Latitude,
    double Longitude,
    bool MembersOnly,
    IReadOnlyList<SnapshotPrice> Prices);

public sealed record SnapshotPrice(short FuelTypeId, decimal? PriceCents, bool IsAvailable);

/// <summary>A station's hours as the API serves them (<see cref="HoursPrecedence"/>'s choice), read and in its time zone.</summary>
/// <param name="Zone">The station's (<see cref="QuebecTime.ZoneFor"/>): its hours are in it.</param>
public sealed record SnapshotHours(StationHours Dto, OpeningHours Hours, TimeZoneInfo Zone)
{
    /// <summary>As the rankings read them, to leave out a station closed on arrival (<see cref="Arrivals.LeaveOutClosed"/>).</summary>
    public ArrivalHours Arrival => new(Hours, Zone, Dto.Trusted, Dto.TemporarilyClosed);
}

/// <summary>A row of <c>station_hours</c>, with where its station is (for its time zone).</summary>
/// <param name="SourceRef">The source's id for the place: for OpenStreetMap, the element (<c>node/6756481766</c>).</param>
/// <param name="MatchedBy">How the row was matched to the station (<see cref="StationHoursEntry.MatchedBy"/>).</param>
/// <param name="SourceBrand">The brand the source writes for the place (<see cref="StationHoursEntry.SourceBrand"/>).</param>
public sealed record SnapshotHoursRow(
    HoursRow Row, double? Latitude, double? Longitude, string? SourceRef = null, string? MatchedBy = null, string? SourceBrand = null);

/// <summary>A station's OpenStreetMap element, as <see cref="StationSnapshot.OsmElementsOf"/> found it.</summary>
/// <param name="Ref"><c>node/123</c>, <c>way/456</c> or <c>relation/789</c>.</param>
/// <param name="BrandDiffers">
/// Matched by place only (<see cref="StationHoursEntry.ByLocation"/>): on OpenStreetMap it's of another brand or none
/// (<see cref="StationDetail.OsmBrandDiffers"/>).
/// </param>
/// <param name="Brand">
/// Its brand there as the last run read it, when a feed station has it (<see cref="StationHoursEntry.SourceBrand"/>,
/// <see cref="StationDetail.OsmBrand"/>): "Sonic"; null for none.
/// </param>
public sealed record SnapshotOsmElement(string Ref, bool BrandDiffers, string? Brand = null);

/// <param name="Version">The import it was loaded for (<see cref="DataVersion"/>); null before the first one.</param>
/// <param name="HoursVersion">
/// The latest hours run it was loaded for (<see cref="DataVersion.HoursLatestAsync"/>), not rounded, so that two runs in
/// the same second are two versions; null before the first one.
/// </param>
/// <param name="Hours">The hours of each station that has some, active or not, by id.</param>
/// <param name="HoursStaleAt">When a source whose hours it holds goes stale, which changes them (<see cref="HoursPrecedence.StaleAt"/>).</param>
/// <param name="HoursChangedAt">
/// When the hours it holds last changed (<see cref="StationSnapshot.HoursChangedAt"/>): what the 304 rule compares with
/// <c>If-Modified-Since</c>. Null before the first hours run.
/// </param>
/// <param name="OsmElements">
/// The OpenStreetMap element each station was matched to (<see cref="StationSnapshot.OsmElementsOf"/>), its hours or not,
/// by id: where the app sends a correction of them.
/// </param>
/// <param name="Corrections">
/// The reviewed position corrections that apply (<see cref="PositionCorrections"/>), by station id: those stations are at
/// the corrected point in <paramref name="Stations"/>, and every answer gives that point instead of the feed's.
/// </param>
/// <param name="PositionsChangedAt">
/// When the corrections that apply last changed, as this process saw it (<see cref="StationSnapshot.PositionsChange"/>):
/// what the 304 rule compares with <c>If-Modified-Since</c> besides the hours, so a new image's corrections aren't hidden
/// by a 304. Null before the first load.
/// </param>
public sealed record Snapshot(
    DateTimeOffset? Version,
    IReadOnlyList<SnapshotStation> Stations,
    DateTimeOffset? HoursVersion,
    IReadOnlyDictionary<long, SnapshotHours> Hours,
    DateTimeOffset? HoursStaleAt,
    DateTimeOffset? HoursChangedAt,
    IReadOnlyDictionary<long, SnapshotOsmElement>? OsmElements = null,
    IReadOnlyDictionary<long, PositionCorrection>? Corrections = null,
    DateTimeOffset? PositionsChangedAt = null)
{
    /// <summary>
    /// When what it serves besides the feed last changed, the hours (<see cref="HoursChangedAt"/>) or the positions
    /// (<see cref="PositionsChangedAt"/>), whichever is later: a client with the feed's version from before doesn't have it.
    /// </summary>
    public DateTimeOffset? ChangedAt => HoursChangedAt is null || PositionsChangedAt > HoursChangedAt ? PositionsChangedAt : HoursChangedAt;

    /// <summary>The station's OpenStreetMap element, <c>node/123</c>, and how it was matched; null when it wasn't matched to one.</summary>
    public SnapshotOsmElement? OsmElementOf(long stationId) => OsmElements?.GetValueOrDefault(stationId);

    /// <summary>
    /// Where to say the station is: the corrected point when a correction applies to it, else the one given (the
    /// database's, which is the feed's). For the answers that read stations from the database rather than from the snapshot.
    /// </summary>
    public (double? Latitude, double? Longitude) PointOf(long stationId, double? latitude, double? longitude) =>
        Corrections?.GetValueOrDefault(stationId) is { } correction ? (correction.Latitude, correction.Longitude) : (latitude, longitude);

    /// <summary>Still what the data says: the same import and hours run, and no source gone stale since.</summary>
    public bool IsCurrent(DateTimeOffset? version, DateTimeOffset? hoursVersion, DateTimeOffset now) =>
        Version == version && HoursVersion == hoursVersion && !(now > HoursStaleAt);
}

/// <summary>
/// Every active station with coordinates and its current prices, in memory: what Best near you ranks. About 2,460 stations,
/// loaded with one query when a new import is in, then shared by every request until the next one. The ranking filters it
/// (status, availability, Costco) itself: <c>/stations/nearby</c> returns only the 100 closest, which in Montréal cover a
/// few km, and doesn't check those. With them, each station's opening hours, chosen among their sources: loaded again
/// when an hours run changed them too, or when a source went stale. Each station is where it is: a reviewed correction's
/// point when one applies (<see cref="PositionCorrections.Embedded"/>), else the feed's, which the database keeps as it is.
/// </summary>
public sealed class StationSnapshot(TimeProvider timeProvider, IConfiguration configuration, ILogger<StationSnapshot> logger)
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly TimeSpan _staleAfter = TimeSpan.FromDays(
        configuration.GetValue("Health:HoursStaleAfterDays", HoursPrecedence.DefaultStaleAfter.TotalDays));

    /// <summary>The corrections already said not to apply, by natural key: each is logged once, until it applies again.</summary>
    private readonly HashSet<string> _notApplying = new(StringComparer.Ordinal);

    private Snapshot? _current;
    private string? _positionsKey;
    private DateTimeOffset? _positionsChangedAt;

    /// <summary>The snapshot of the latest import and hours, loaded by the first request that needs it while the others wait.</summary>
    public async Task<Snapshot> GetAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        // Read before the stations: an import committing in between makes the data newer than its version, which costs one
        // extra load, never stale data kept.
        var version = await DataVersion.LatestAsync(db, cancellationToken);
        var hoursVersion = await DataVersion.HoursLatestAsync(db, cancellationToken);
        if (_current is { } current && current.IsCurrent(version, hoursVersion, timeProvider.GetUtcNow()))
        {
            return current;
        }

        await _loading.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_current is { } loaded && loaded.IsCurrent(version, hoursVersion, now))
            {
                return loaded;
            }

            var rows = await db.Stations
                .Where(s => s.IsActive && s.Latitude != null && s.Longitude != null)
                .Select(s => new
                {
                    s.Id,
                    s.NaturalKey,
                    s.Name,
                    s.Brand,
                    s.Status,
                    s.Address,
                    Latitude = s.Latitude!.Value,
                    Longitude = s.Longitude!.Value,
                    Prices = s.CurrentPrices.Select(p => new SnapshotPrice(p.FuelTypeId, p.PriceCents, p.IsAvailable)).ToList(),
                })
                .ToListAsync(cancellationToken);

            var corrections = PositionCorrections.Embedded;
            var (stations, corrected) = Correct(
                rows.Select(s => (
                    new SnapshotStation(
                        s.Id, s.Name, s.Brand, s.Status, s.Address, s.Latitude, s.Longitude, Brands.IsMembersOnly(s.Brand), s.Prices),
                    s.NaturalKey)),
                corrections);
            LogNotApplying(corrections, rows.ToDictionary(s => s.NaturalKey, s => (s.Latitude, s.Longitude), StringComparer.Ordinal));
            (_positionsKey, _positionsChangedAt) = PositionsChange(_positionsKey, _positionsChangedAt, corrected, now);

            var lastSuccesses = await LastSuccessesAsync(db, cancellationToken);
            var hourRows = await db.StationHours
                .Select(h => new SnapshotHoursRow(
                    new HoursRow(h.StationId, h.Source, h.OpeningHours, h.Confirmed, h.TemporarilyClosed, h.SourceCheckedAt, h.SourceUrl),
                    h.Station.Latitude,
                    h.Station.Longitude,
                    h.SourceRef,
                    h.MatchedBy,
                    h.SourceBrand))
                .ToListAsync(cancellationToken);
            var hours = ChooseHours(Correct(hourRows, corrected), lastSuccesses, now, _staleAfter);
            return _current = new Snapshot(
                version,
                stations,
                hoursVersion,
                hours,
                HoursPrecedence.StaleAt(lastSuccesses, now, _staleAfter),
                HoursChangedAt(hoursVersion, lastSuccesses, now, _staleAfter),
                OsmElementsOf(hourRows),
                corrected,
                _positionsChangedAt);
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>
    /// The stations where they are: those whose correction applies to their feed point
    /// (<see cref="PositionCorrection.AppliesTo"/>) at the corrected point, the others as given; and the corrections that
    /// apply, by station id.
    /// </summary>
    public static (IReadOnlyList<SnapshotStation> Stations, IReadOnlyDictionary<long, PositionCorrection> Corrected) Correct(
        IEnumerable<(SnapshotStation Station, string NaturalKey)> stations, PositionCorrections corrections)
    {
        var placed = new List<SnapshotStation>();
        var corrected = new Dictionary<long, PositionCorrection>();
        foreach (var (station, naturalKey) in stations)
        {
            if (corrections.AppliedTo(naturalKey, station.Latitude, station.Longitude) is { } correction)
            {
                corrected[station.Id] = correction;
                placed.Add(station with { Latitude = correction.Latitude, Longitude = correction.Longitude });
            }
            else
            {
                placed.Add(station);
            }
        }

        return (placed, corrected);
    }

    /// <summary>The hours rows with their station where it is (<paramref name="corrected"/>): its time zone is the one there.</summary>
    public static IReadOnlyList<SnapshotHoursRow> Correct(
        IEnumerable<SnapshotHoursRow> rows, IReadOnlyDictionary<long, PositionCorrection> corrected) =>
        [.. rows.Select(r => corrected.GetValueOrDefault(r.Row.StationId) is { } c ? r with { Latitude = c.Latitude, Longitude = c.Longitude } : r)];

    /// <summary>
    /// What the corrections that apply are (<paramref name="corrected"/>, as a key that changes with them), and since
    /// when: <paramref name="lastChangedAt"/> while they're the same as at the last load, else now. The first load of a
    /// process is a change, since a new image may bring new ones: a client with the feed's version from before the start
    /// gets no 304 until the next import.
    /// </summary>
    public static (string Key, DateTimeOffset ChangedAt) PositionsChange(
        string? lastKey, DateTimeOffset? lastChangedAt, IReadOnlyDictionary<long, PositionCorrection> corrected, DateTimeOffset now)
    {
        var key = string.Join(
            ";",
            corrected.OrderBy(c => c.Key).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key}:{c.Value.Latitude:R},{c.Value.Longitude:R}")));
        return key == lastKey && lastChangedAt is { } at ? (key, at) : (key, now);
    }

    /// <summary>Logs, once each, the corrections that don't apply: their station's feed point moved, or no active station has their key.</summary>
    private void LogNotApplying(PositionCorrections corrections, IReadOnlyDictionary<string, (double Latitude, double Longitude)> feedPoints)
    {
        foreach (var correction in corrections.All)
        {
            var point = feedPoints.TryGetValue(correction.NaturalKey, out var p) ? (p.Latitude, p.Longitude) : ((double?, double?)?)null;
            var state = PositionCorrections.StateOf(correction, point);
            if (state == PositionCorrectionState.Applied)
            {
                _notApplying.Remove(correction.NaturalKey);
            }
            else if (_notApplying.Add(correction.NaturalKey))
            {
                if (state == PositionCorrectionState.Expired)
                {
                    logger.LogWarning(
                        "The position correction of {NaturalKey} has expired: the Régie's point moved since it was reviewed. The feed's point is used again; take it out of curated-positions.json.",
                        correction.NaturalKey);
                }
                else
                {
                    logger.LogWarning(
                        "The position correction of {NaturalKey} applies to no station on the map: it left the feed, has no point, or its address changed. Take it out of curated-positions.json.",
                        correction.NaturalKey);
                }
            }
        }
    }

    /// <summary>Each station's hours as served: <see cref="HoursPrecedence.Choose"/>'s, in the contract's shape, in its zone.</summary>
    public static IReadOnlyDictionary<long, SnapshotHours> ChooseHours(
        IReadOnlyCollection<SnapshotHoursRow> rows, IReadOnlyDictionary<string, DateTimeOffset> lastSuccesses, DateTimeOffset now, TimeSpan staleAfter)
    {
        var where = new Dictionary<long, (double? Latitude, double? Longitude)>(rows.Count);
        foreach (var row in rows)
        {
            where[row.Row.StationId] = (row.Latitude, row.Longitude);
        }

        return HoursPrecedence.Choose(rows.Select(r => r.Row), lastSuccesses, now, staleAfter).ToDictionary(
            c => c.Key,
            c =>
            {
                var (row, hours, trusted) = c.Value;
                var (latitude, longitude) = where[c.Key];
                return new SnapshotHours(
                    new StationHours(hours.ToRule(), row.Source, trusted, row.Confirmed, row.CheckedAt, row.Link, row.TemporarilyClosed),
                    hours,
                    QuebecTime.ZoneFor(latitude, longitude));
            });
    }

    /// <summary>
    /// Each station's OpenStreetMap element, from its <c>osm</c> row, with a rule or without one (OpenStreetMap often has the
    /// place but not its hours): <c>node/123</c>, <c>way/456</c> or <c>relation/789</c>, and whether it was matched by place
    /// only (<see cref="StationHoursEntry.ByLocation"/>), its brand there another or none, with that brand as it's written
    /// there. A reference of another shape is left out, so the app is never sent elsewhere than to an element.
    /// </summary>
    public static IReadOnlyDictionary<long, SnapshotOsmElement> OsmElementsOf(IEnumerable<SnapshotHoursRow> rows)
    {
        var elements = new Dictionary<long, SnapshotOsmElement>();
        foreach (var row in rows)
        {
            if (row.Row.Source == HoursPrecedence.Osm && IsOsmElement(row.SourceRef))
            {
                elements[row.Row.StationId] = new SnapshotOsmElement(
                    row.SourceRef!, row.MatchedBy == StationHoursEntry.ByLocation, string.IsNullOrWhiteSpace(row.SourceBrand) ? null : row.SourceBrand);
            }
        }

        return elements;
    }

    /// <summary><c>node/123</c>, <c>way/456</c>, <c>relation/789</c>: a type, and an id above 0 in digits.</summary>
    public static bool IsOsmElement(string? reference) =>
        reference?.Split('/') is [var type and ("node" or "way" or "relation"), var id]
        && id.Length > 0 && id.All(char.IsAsciiDigit) && long.TryParse(id, out var number) && number > 0;

    /// <summary>
    /// When the hours served last changed: the latest hours run that changed them (<paramref name="hoursVersion"/>), or the
    /// latest moment a source went stale (<see cref="HoursPrecedence.StaleSince"/>), which changes them with no run, so a
    /// client that has them from before mustn't get a 304 for them.
    /// </summary>
    public static DateTimeOffset? HoursChangedAt(
        DateTimeOffset? hoursVersion, IReadOnlyDictionary<string, DateTimeOffset> lastSuccesses, DateTimeOffset now, TimeSpan staleAfter) =>
        HoursPrecedence.StaleSince(lastSuccesses, now, staleAfter) is { } stale && !(hoursVersion >= stale) ? stale : hoursVersion;

    /// <summary>When each hours source last succeeded (<see cref="HoursHistory"/>), those turned off left out: they have no rows.</summary>
    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastSuccessesAsync(EssenceDbContext db, CancellationToken cancellationToken) =>
        (await HoursHistory.LoadAsync(db, cancellationToken))
            .Where(h => h.LastSuccess is not null)
            .ToDictionary(h => h.Source, h => h.LastSuccess!.FinishedAt);
}
