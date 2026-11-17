using Essence.Contracts;
using Essence.Data;
using Essence.Hours;
using Essence.Ranking;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>An active station with coordinates and its current prices, as <see cref="StationSnapshot"/> keeps it.</summary>
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
public sealed record SnapshotHoursRow(HoursRow Row, double? Latitude, double? Longitude);

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
public sealed record Snapshot(
    DateTimeOffset? Version,
    IReadOnlyList<SnapshotStation> Stations,
    DateTimeOffset? HoursVersion,
    IReadOnlyDictionary<long, SnapshotHours> Hours,
    DateTimeOffset? HoursStaleAt,
    DateTimeOffset? HoursChangedAt)
{
    /// <summary>Still what the data says: the same import and hours run, and no source gone stale since.</summary>
    public bool IsCurrent(DateTimeOffset? version, DateTimeOffset? hoursVersion, DateTimeOffset now) =>
        Version == version && HoursVersion == hoursVersion && !(now > HoursStaleAt);
}

/// <summary>
/// Every active station with coordinates and its current prices, in memory: what Best near you ranks. About 2,460 stations,
/// loaded with one query when a new import is in, then shared by every request until the next one. The ranking filters it
/// (status, availability, Costco) itself: <c>/stations/nearby</c> returns only the 100 closest, which in Montréal cover a
/// few km, and doesn't check those. With them, each station's opening hours, chosen among their sources: loaded again
/// when an hours run changed them too, or when a source went stale.
/// </summary>
public sealed class StationSnapshot(TimeProvider timeProvider, IConfiguration configuration)
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly TimeSpan _staleAfter = TimeSpan.FromDays(
        configuration.GetValue("Health:HoursStaleAfterDays", HoursPrecedence.DefaultStaleAfter.TotalDays));

    private Snapshot? _current;

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
                    s.Name,
                    s.Brand,
                    s.Status,
                    s.Address,
                    Latitude = s.Latitude!.Value,
                    Longitude = s.Longitude!.Value,
                    Prices = s.CurrentPrices.Select(p => new SnapshotPrice(p.FuelTypeId, p.PriceCents, p.IsAvailable)).ToList(),
                })
                .ToListAsync(cancellationToken);

            var stations = rows
                .Select(s => new SnapshotStation(
                    s.Id, s.Name, s.Brand, s.Status, s.Address, s.Latitude, s.Longitude, Brands.IsMembersOnly(s.Brand), s.Prices))
                .ToList();

            var lastSuccesses = await LastSuccessesAsync(db, cancellationToken);
            var hourRows = await db.StationHours
                .Select(h => new SnapshotHoursRow(
                    new HoursRow(h.StationId, h.Source, h.OpeningHours, h.Confirmed, h.TemporarilyClosed, h.SourceCheckedAt, h.SourceUrl),
                    h.Station.Latitude,
                    h.Station.Longitude))
                .ToListAsync(cancellationToken);
            var hours = ChooseHours(hourRows, lastSuccesses, now, _staleAfter);
            return _current = new Snapshot(
                version,
                stations,
                hoursVersion,
                hours,
                HoursPrecedence.StaleAt(lastSuccesses, now, _staleAfter),
                HoursChangedAt(hoursVersion, lastSuccesses, now, _staleAfter));
        }
        finally
        {
            _loading.Release();
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
