using Essence.Data;
using Essence.Hours;
using Essence.Ingest.Hours;
using Essence.Ranking;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Positions;

/// <summary>
/// Once a day (<see cref="PositionsOptions.RunAt"/>, and after a start), lists the stations the Régie's feed may put in
/// the wrong place, for a person to review (<c>/stations/positions</c>): it compares each station's feed point with
/// OpenStreetMap's elements and the brands' locators in the hours sources' last downloads kept on disk (nothing is
/// fetched), and, for the few that have such a position far off, with Photon's point for its address
/// (<see cref="PositionsOptions.GeocodingUrl"/>). The candidates replace the last run's (<see cref="PositionFinder"/>).
/// Daily rather than after each hours re-match: the downloads change once a week and the feed's points hardly ever, a
/// person reads the list, and the lookups stay a few dozen a day. Stations whose correction applies are left out, and
/// each correction that expired, or whose station is gone, is logged at each run, to be taken out of the file.
/// </summary>
public sealed class PositionsWorker(
    IServiceScopeFactory scopeFactory,
    HoursCache cache,
    TimeProvider timeProvider,
    IOptions<PositionsOptions> options,
    ILogger<PositionsWorker> logger) : BackgroundService
{
    /// <summary>How long after a start its run comes, the feed's first import and the hours' runs on start first.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    /// <summary>Photon's answers are read in French: the feed's addresses are.</summary>
    private const string Language = "fr";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogInformation("Finding stations drawn in the wrong place is off (Positions:Enabled).");
            return;
        }

        try
        {
            var next = o.RunOnStart ? timeProvider.GetUtcNow() + StartDelay : NextRun(timeProvider.GetUtcNow(), o.RunAt);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = next - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }

                await RunAsync(stoppingToken);
                next = NextRun(timeProvider.GetUtcNow(), o.RunAt);
                logger.LogDebug("Next check of the stations' positions at {NextRunAt:O}.", next);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>The next <paramref name="runAt"/> in Québec time after <paramref name="now"/>: today's, or tomorrow's once it's past.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeSpan runAt)
    {
        var zone = QuebecTime.Eastern;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var at = HoursSchedule.Instant(today + runAt, zone);
        return at > now ? at : HoursSchedule.Instant(today.AddDays(1) + runAt, zone);
    }

    /// <summary>One run; a failure is logged, and the next day's run tries again.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<PositionsWriter>();
            var corrections = PositionCorrections.Embedded;
            foreach (var warning in corrections.Warnings)
            {
                logger.LogWarning("curated-positions.json: {Warning}", warning);
            }

            var stations = await writer.LoadStationsAsync(cancellationToken);
            var (applied, expired, gone) = LogCorrections(corrections, stations.FeedPoints);
            var sources = await LoadSourcesAsync(scope, cancellationToken);
            var finder = new PositionFinder(stations.Stations, sources);

            var photon = options.Value.GeocodingUri is null ? null : scope.ServiceProvider.GetService<PhotonClient>();
            var found = new List<FoundPosition>();
            var (withPosition, geocoded) = (0, 0);
            foreach (var station in stations.Stations)
            {
                if (corrections.AppliedTo(station.NaturalKey, station.Latitude, station.Longitude) is not null)
                {
                    continue;
                }

                var positions = finder.Known(station);
                withPosition += positions.Count > 0 ? 1 : 0;
                if (photon is not null && finder.NeedsGeocode(station, positions))
                {
                    try
                    {
                        var places = await photon.SearchAsync(
                            new PhotonSearch(PositionFinder.GeocodeQuery(station.Address), Language, null, PositionFinder.GeocodeLimit), cancellationToken);
                        positions = [.. positions, .. finder.FromPhoton(station, places)];
                        geocoded++;
                    }
                    catch (PhotonException ex) when (ex.Failure == PhotonFailure.Unavailable)
                    {
                        logger.LogWarning(
                            "The address search doesn't answer ({Message}): this run compares OpenStreetMap and the locators only.", ex.Message);
                        photon = null;
                    }
                    catch (PhotonException ex)
                    {
                        logger.LogWarning("The address search refused the address of station {StationId}: {Message}", station.Id, ex.Message);
                    }
                }

                if (finder.Candidate(station, positions) is { } candidate)
                {
                    found.Add(candidate);
                }
            }

            found.Sort((a, b) => b.DistanceM != a.DistanceM ? b.DistanceM.CompareTo(a.DistanceM) : a.Station.Id.CompareTo(b.Station.Id));
            await writer.ReplaceAsync(found, timeProvider.GetUtcNow(), cancellationToken);
            foreach (var candidate in found)
            {
                logger.LogInformation(
                    "Station {StationId} ({NaturalKey}) may be drawn {DistanceM} m from where it is: {Evidence} agree.",
                    candidate.Station.Id, candidate.Station.NaturalKey, candidate.DistanceM,
                    string.Join(", ", candidate.Evidence.Select(e => $"{e.Kind} {e.Ref}")));
            }

            logger.LogInformation(
                "Stations' positions checked: {Stations} stations, {WithPosition} with a position from OpenStreetMap or a locator, " +
                "{Geocoded} addresses looked up, {Candidates} to review (/stations/positions); corrections: {Applied} applied, " +
                "{Expired} expired, {Gone} of stations gone.",
                stations.Stations.Count, withPosition, geocoded, found.Count, applied, expired, gone);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not check the stations' positions; the next run tries again.");
        }
    }

    /// <summary>Says which corrections no longer apply, one line each, and counts them.</summary>
    private (int Applied, int Expired, int Gone) LogCorrections(
        PositionCorrections corrections, IReadOnlyDictionary<string, (double? Latitude, double? Longitude)> feedPoints)
    {
        var (applied, expired, gone) = (0, 0, 0);
        foreach (var correction in corrections.All)
        {
            var point = feedPoints.TryGetValue(correction.NaturalKey, out var p) ? p : ((double?, double?)?)null;
            switch (PositionCorrections.StateOf(correction, point))
            {
                case PositionCorrectionState.Applied:
                    applied++;
                    break;
                case PositionCorrectionState.Expired:
                    expired++;
                    logger.LogWarning(
                        "The position correction of {NaturalKey} has expired: the Régie's point moved since it was reviewed ({Reviewed}). " +
                        "It's no longer applied; take it out of curated-positions.json.",
                        correction.NaturalKey, correction.Reviewed);
                    break;
                default:
                    gone++;
                    logger.LogWarning(
                        "The position correction of {NaturalKey} applies to no active station: it left the feed, or its address changed. " +
                        "Take it out of curated-positions.json.",
                        correction.NaturalKey);
                    break;
            }
        }

        return (applied, expired, gone);
    }

    /// <summary>
    /// OpenStreetMap's elements and the brands' locators, as their last downloads kept on disk have them; the sources
    /// turned off, those without a download yet and those whose download can't be read are left out.
    /// </summary>
    private async Task<IReadOnlyList<PositionSource>> LoadSourcesAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var sources = new List<PositionSource>();
        foreach (var source in scope.ServiceProvider.GetServices<IHoursSource>().Where(s => s.Enabled && !s.ShippedInImage))
        {
            try
            {
                if (await cache.LoadAsync(source.Name, cancellationToken) is not { } download)
                {
                    logger.LogInformation("No download of {Source} is kept yet: its positions aren't compared.", source.Name);
                    continue;
                }

                sources.Add(new PositionSource(source.Name, source.Name == OverpassSource.SourceName, source.Parse(download.Raw).Records));
            }
            catch (Exception ex) when (ex is HoursFormatException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The download of {Source} kept on disk can't be read: its positions aren't compared.", source.Name);
            }
        }

        return sources;
    }
}
