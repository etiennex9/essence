using System.Globalization;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Essence.Hours;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>A station as the review of positions reads it: where the feed puts it, whatever corrects it.</summary>
public sealed record ReviewStation(
    long Id, string NaturalKey, string? Name, string Address, string? Brand, bool IsActive, double? Latitude, double? Longitude);

/// <summary>
/// <c>/stations/positions</c>: the reviewed position corrections (<see cref="PositionCorrections"/>) and what became of
/// each, and the candidates the ingest found (<c>position_candidates</c>), each with the entry to paste into
/// <c>curated-positions.json</c> once checked.
/// </summary>
public static class PositionReview
{
    public const string Applied = "applied";
    public const string Expired = "expired";
    public const string StationGone = "station gone";

    /// <summary>How the brands' locators are named in a note.</summary>
    private static readonly Dictionary<string, string> Locators = new(StringComparer.Ordinal)
    {
        ["costco"] = "Costco",
        ["circlek"] = "Circle K",
        ["ultramar"] = "Ultramar",
        ["shell"] = "Shell",
    };

    /// <summary>Reads the stations and candidates, and puts the review together for <paramref name="today"/> (Québec's day).</summary>
    public static async Task<StationPositions> LoadAsync(
        EssenceDbContext db, PositionCorrections corrections, DateOnly today, CancellationToken cancellationToken)
    {
        var candidates = await db.PositionCandidates.AsNoTracking().ToListAsync(cancellationToken);
        var keys = corrections.All.Select(c => c.NaturalKey).ToList();
        var ids = candidates.Select(c => c.StationId).ToList();
        var stations = await db.Stations
            .AsNoTracking()
            .Where(s => keys.Contains(s.NaturalKey) || ids.Contains(s.Id))
            .Select(s => new ReviewStation(s.Id, s.NaturalKey, s.Name, s.Address, s.Brand, s.IsActive, s.Latitude, s.Longitude))
            .ToListAsync(cancellationToken);
        return Build(corrections, stations, candidates, today);
    }

    /// <param name="stations">The stations with a correction's natural key, and the candidates' stations.</param>
    public static StationPositions Build(
        PositionCorrections corrections, IReadOnlyList<ReviewStation> stations, IReadOnlyList<PositionCandidate> candidates, DateOnly today)
    {
        var byKey = stations.ToLookup(s => s.NaturalKey, StringComparer.Ordinal);
        var byId = stations.ToDictionary(s => s.Id);

        var corrected = new List<PositionCorrectionDto>();
        foreach (var correction in corrections.All)
        {
            var station = byKey[correction.NaturalKey].OrderByDescending(s => s.IsActive).FirstOrDefault();
            var active = station is { IsActive: true } ? station : null;
            var state = PositionCorrections.StateOf(correction, active is null ? null : (active.Latitude, active.Longitude));
            var feed = active is { Latitude: { } latitude, Longitude: { } longitude }
                ? new PositionPoint(latitude, longitude)
                : new PositionPoint(correction.FeedLatitude, correction.FeedLongitude);
            corrected.Add(new PositionCorrectionDto(
                station?.Id,
                correction.NaturalKey,
                station?.Name,
                station?.Address,
                feed,
                new PositionPoint(correction.Latitude, correction.Longitude),
                Metres(feed.Latitude, feed.Longitude, correction.Latitude, correction.Longitude),
                state switch
                {
                    PositionCorrectionState.Applied => Applied,
                    PositionCorrectionState.Expired => Expired,
                    _ => StationGone,
                },
                Day(correction.Reviewed),
                correction.Note));
        }

        var review = new List<PositionCandidateDto>();
        foreach (var candidate in candidates.OrderByDescending(c => c.DistanceM).ThenBy(c => c.StationId))
        {
            // Approved since it was found: the station is where the correction says.
            if (!byId.TryGetValue(candidate.StationId, out var station)
                || corrections.AppliedTo(station.NaturalKey, station.Latitude, station.Longitude) is not null)
            {
                continue;
            }

            var evidence = PositionCandidate.Read(candidate.Evidence);
            review.Add(new PositionCandidateDto(
                station.Id,
                candidate.NaturalKey,
                station.Name,
                station.Address,
                station.Brand,
                new PositionPoint(candidate.FeedLatitude, candidate.FeedLongitude),
                new PositionPoint(candidate.Latitude, candidate.Longitude),
                candidate.DistanceM,
                [.. evidence.Select(e => new PositionEvidenceDto(e.Kind, e.Ref, e.Latitude, e.Longitude, e.DistanceM))],
                new PositionEntry(
                    candidate.NaturalKey, candidate.Latitude, candidate.Longitude, candidate.FeedLatitude, candidate.FeedLongitude, Day(today),
                    Note(evidence, candidate.DistanceM)),
                candidate.FoundAt));
        }

        return new StationPositions(corrected, review, corrections.Warnings);
    }

    /// <summary>
    /// The evidence in a sentence, for the entry's note: "OpenStreetMap node/5312347403 and the address node/1234 (Photon)
    /// agree within 4 m, 653 m from the Régie's point." Element and store ids only: never a name, which can be a person's.
    /// </summary>
    public static string Note(IReadOnlyList<PositionEvidence> evidence, int distanceM)
    {
        var named = evidence.Select(e => e.Kind switch
        {
            PositionCandidate.FromOsm => $"OpenStreetMap {e.Ref}",
            PositionCandidate.FromLocator when e.Ref.Split('/', 2) is [var source, var id] => $"{Locators.GetValueOrDefault(source, source)}'s locator ({id})",
            PositionCandidate.FromPhoton => $"the address {e.Ref} (Photon)",
            _ => $"{e.Kind} {e.Ref}",
        }).ToList();
        var list = named.Count switch
        {
            0 => "Nothing",
            1 => named[0],
            _ => string.Join(", ", named[..^1]) + " and " + named[^1],
        };
        var apart = 0;
        for (var i = 0; i < evidence.Count; i++)
        {
            for (var j = i + 1; j < evidence.Count; j++)
            {
                apart = Math.Max(apart, Metres(evidence[i].Latitude, evidence[i].Longitude, evidence[j].Latitude, evidence[j].Longitude));
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{list} agree within {apart} m, {distanceM} m from the Régie's point.");
    }

    /// <summary>Today in Québec, the review's day.</summary>
    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, QuebecTime.Eastern).DateTime);

    private static int Metres(double latitude1, double longitude1, double latitude2, double longitude2) =>
        (int)Math.Round(PositionCorrections.DistanceM(latitude1, longitude1, latitude2, longitude2));

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
