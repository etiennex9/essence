using System.Globalization;
using System.Text.RegularExpressions;
using Essence.Data.Entities;

namespace Essence.Ingest.Hours;

/// <summary>A feed station, as matching reads it.</summary>
/// <param name="BrandFamily">Its brand, folded (<see cref="Hours.BrandFamily.Of"/>); null when it has none.</param>
/// <param name="HouseNumber">Its address's number (<see cref="HoursMatcher.HouseNumber"/>).</param>
/// <param name="Postcode">Its postal code, folded: <c>H7P5P4</c>.</param>
public sealed record MatchStation(
    long Id,
    string NaturalKey,
    string? BrandFamily,
    double? Latitude,
    double? Longitude,
    string? HouseNumber,
    string Postcode)
{
    /// <summary>Couche-Tard's store number in its name (<see cref="HoursMatcher.StoreNumber"/>): 140 for "Couche-Tard #140".</summary>
    public int? StoreNumber { get; init; }
}

/// <summary>A record matched to a station: one row of <c>station_hours</c>.</summary>
/// <param name="MatchedBy">
/// <see cref="StationHoursEntry.ByNaturalKey"/>, <see cref="StationHoursEntry.ByStoreNumber"/>,
/// <see cref="StationHoursEntry.ByDistance"/> or <see cref="StationHoursEntry.ByBrand"/>.
/// </param>
/// <param name="DistanceM">From the record's point to the station's, for a match by distance or by store number.</param>
/// <param name="Confirmed">As <see cref="StationHoursEntry.Confirmed"/>.</param>
public sealed record HoursMatch(long StationId, SourceRecord Record, string MatchedBy, int? DistanceM, bool Confirmed);

/// <param name="Matches">At most one per station.</param>
/// <param name="Unmatched">Records for one place (a natural key, a store number or a point) that found no station.</param>
/// <param name="Ambiguous">Of those, the ones left out because which station they're about couldn't be told.</param>
public sealed record MatchResult(IReadOnlyList<HoursMatch> Matches, int Unmatched, int Ambiguous);

/// <summary>
/// Gives each station at most one of a source's records, never guessing. A rule written for one station (by its natural
/// key) comes first, then Couche-Tard's stores by the number in the stations' names, then the records with a point, then
/// a brand's rule for the brand's stations left.
/// <para>
/// A store number matches the one station named after it, whatever its brand and wherever it is (the feed's point and
/// the store's can be 700 m apart), and never by distance: the store nearest a Couche-Tard station is another
/// station's three times out of four. A number two stations carry matches neither.
/// </para>
/// <para>
/// A record with a point matches a station of its own brand family within the source's radius (Costco's, whatever the
/// source, within <see cref="CostcoReachM"/>), never another brand's, and the match is confirmed when their postal codes
/// or house numbers agree. Pairs are taken one to one, the confirmed first, then the closest. A pair that isn't confirmed
/// is left out while there's a rival still free: the same record within reach of another station, or the same station
/// within reach of another record with other hours. Which is which can't be told then.
/// </para>
/// </summary>
public static partial class HoursMatcher
{
    /// <summary>
    /// How far a Costco record may be from its station, whatever the source: the feed's point is on the warehouse, and its
    /// gas bar can be 540 m away (Drummondville's).
    /// </summary>
    public const double CostcoReachM = 600;

    private const double EarthRadiusM = 6_371_008.8;

    public static MatchResult Match(IReadOnlyList<SourceRecord> records, IReadOnlyList<MatchStation> stations, double radiusM)
    {
        var matches = new List<HoursMatch>();
        var taken = new HashSet<long>();
        var unmatched = 0;

        var byKey = new Dictionary<string, MatchStation>(StringComparer.Ordinal);
        foreach (var station in stations)
        {
            byKey.TryAdd(station.NaturalKey, station);
        }

        foreach (var record in records.Where(r => r.NaturalKey is not null))
        {
            if (byKey.TryGetValue(record.NaturalKey!, out var station) && taken.Add(station.Id))
            {
                matches.Add(new HoursMatch(station.Id, record, StationHoursEntry.ByNaturalKey, null, Confirmed: true));
            }
            else
            {
                unmatched++;
            }
        }

        var ambiguous = 0;
        var byNumber = stations.Where(s => s.StoreNumber is not null).ToLookup(s => s.StoreNumber!.Value);
        foreach (var record in records.Where(r => r.NaturalKey is null && r.StoreNumber is not null))
        {
            var named = byNumber[record.StoreNumber!.Value].ToList();
            if (named.Count == 1 && taken.Add(named[0].Id))
            {
                var distance = Distance(record, named[0]) is { } metres ? (int?)Math.Round(metres) : null;
                matches.Add(new HoursMatch(named[0].Id, record, StationHoursEntry.ByStoreNumber, distance, Confirmed: true));
                continue;
            }

            unmatched++;
            ambiguous += named.Count > 1 ? 1 : 0;
        }

        var located = records
            .Where(r => r.NaturalKey is null && r.StoreNumber is null && r.Latitude is not null && r.Longitude is not null)
            .ToList();
        var (byDistance, ambiguousByDistance) = MatchByDistance(located, stations.Where(s => !taken.Contains(s.Id)).ToList(), radiusM);
        foreach (var match in byDistance)
        {
            taken.Add(match.StationId);
            matches.Add(match);
        }

        unmatched += located.Count - byDistance.Count;
        ambiguous += ambiguousByDistance;

        var brandRules = records
            .Where(r => r.NaturalKey is null && r.StoreNumber is null && (r.Latitude is null || r.Longitude is null) && r.BrandFamily is not null)
            .DistinctBy(r => r.BrandFamily);
        foreach (var rule in brandRules)
        {
            foreach (var station in stations)
            {
                if (station.BrandFamily == rule.BrandFamily && taken.Add(station.Id))
                {
                    matches.Add(new HoursMatch(station.Id, rule, StationHoursEntry.ByBrand, null, Confirmed: true));
                }
            }
        }

        return new MatchResult(matches, unmatched, ambiguous);
    }

    /// <summary>
    /// Couche-Tard's store number in a feed station's name: "Couche-Tard #140" and "Couche-Tard # 140" are 140. Null for
    /// any other name ("Couche-Tard inc.", "2642-4101 Québec Inc."), whatever the station's brand: Couche-Tard's stores sell
    /// Esso's, Irving's, Shell's… gas under their own name.
    /// </summary>
    public static int? StoreNumber(string? name) =>
        name is not null && StoreNumberPattern().Match(name) is { Success: true } match
        && int.TryParse(match.Groups["number"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;

    /// <summary>The number an address starts with: "2999 aut. 440, Laval" is <c>2999</c>; null when it doesn't start with one.</summary>
    public static string? HouseNumber(string? address)
    {
        var text = address?.TrimStart() ?? "";
        var length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        if (length == 0)
        {
            return null;
        }

        var number = text[..length].TrimStart('0');
        return number.Length > 0 ? number : "0";
    }

    /// <summary>The great-circle distance, in metres.</summary>
    public static double DistanceM(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var phi1 = double.DegreesToRadians(latitude1);
        var phi2 = double.DegreesToRadians(latitude2);
        var halfDPhi = (phi2 - phi1) / 2;
        var halfDLambda = double.DegreesToRadians(longitude2 - longitude1) / 2;
        var h = Math.Sin(halfDPhi) * Math.Sin(halfDPhi) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(halfDLambda) * Math.Sin(halfDLambda);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static (List<HoursMatch> Matches, int Ambiguous) MatchByDistance(
        List<SourceRecord> records, List<MatchStation> stations, double radiusM)
    {
        var pairs = new List<Pair>();
        for (var r = 0; r < records.Count; r++)
        {
            var record = records[r];
            foreach (var station in stations)
            {
                if (record.BrandFamily is null || station.BrandFamily != record.BrandFamily
                    || station.Latitude is not { } latitude || station.Longitude is not { } longitude)
                {
                    continue;
                }

                var reach = record.BrandFamily == BrandFamily.Costco ? Math.Max(radiusM, CostcoReachM) : radiusM;
                var distance = DistanceM(record.Latitude!.Value, record.Longitude!.Value, latitude, longitude);
                if (distance <= reach)
                {
                    var confirmed = record.Postcode is { Length: > 0 } postcode && postcode == station.Postcode
                        || record.HouseNumber is { Length: > 0 } number && number == station.HouseNumber;
                    pairs.Add(new Pair(r, station.Id, distance, confirmed));
                }
            }
        }

        var ordered = pairs
            .OrderByDescending(p => p.Confirmed)
            .ThenBy(p => p.DistanceM)
            .ThenBy(p => p.Record)
            .ThenBy(p => p.StationId)
            .ToList();
        var matchedRecords = new HashSet<int>();
        var matchedStations = new HashSet<long>();
        var leftOut = new HashSet<int>();
        var matches = new List<HoursMatch>();
        foreach (var pair in ordered)
        {
            if (matchedRecords.Contains(pair.Record) || matchedStations.Contains(pair.StationId))
            {
                continue;
            }

            if (!pair.Confirmed && ordered.Any(other => IsRival(pair, other)))
            {
                leftOut.Add(pair.Record);
                continue;
            }

            matchedRecords.Add(pair.Record);
            matchedStations.Add(pair.StationId);
            matches.Add(new HoursMatch(pair.StationId, records[pair.Record], StationHoursEntry.ByDistance, (int)Math.Round(pair.DistanceM), pair.Confirmed));
        }

        return (matches, leftOut.Count(r => !matchedRecords.Contains(r)));

        bool IsRival(Pair pair, Pair other) =>
            other != pair && !matchedRecords.Contains(other.Record) && !matchedStations.Contains(other.StationId)
            && (other.Record == pair.Record
                || other.StationId == pair.StationId && Disagree(records[other.Record], records[pair.Record]));
    }

    private static bool Disagree(SourceRecord a, SourceRecord b) => a.Rule != b.Rule || a.TemporarilyClosed != b.TemporarilyClosed;

    private static double? Distance(SourceRecord record, MatchStation station) =>
        record is { Latitude: { } latitude, Longitude: { } longitude } && station is { Latitude: { } stationLatitude, Longitude: { } stationLongitude }
            ? DistanceM(latitude, longitude, stationLatitude, stationLongitude)
            : null;

    [GeneratedRegex(@"^\s*couche[\s-]*tard\s*#\s*(?<number>\d{1,6})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoreNumberPattern();

    /// <param name="Record">The record's index among those with a point.</param>
    private sealed record Pair(int Record, long StationId, double DistanceM, bool Confirmed);
}
