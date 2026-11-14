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
/// <param name="Record">The record, without its hours for a match by location (<see cref="StationHoursEntry.ByLocation"/>).</param>
/// <param name="MatchedBy">
/// <see cref="StationHoursEntry.ByNaturalKey"/>, <see cref="StationHoursEntry.ByStoreNumber"/>,
/// <see cref="StationHoursEntry.ByDistance"/>, <see cref="StationHoursEntry.ByAddress"/>,
/// <see cref="StationHoursEntry.ByBrand"/> or <see cref="StationHoursEntry.ByLocation"/>.
/// </param>
/// <param name="DistanceM">From the record's point to the station's, for a match by distance, address, location or store number.</param>
/// <param name="Confirmed">As <see cref="StationHoursEntry.Confirmed"/>.</param>
public sealed record HoursMatch(long StationId, SourceRecord Record, string MatchedBy, int? DistanceM, bool Confirmed)
{
    /// <summary>
    /// The record is the station's by what it says of it, not only by where it is: every match but one by location,
    /// which only says which element to edit. Those are what <c>hours_imports.matched</c> records and the gate compares
    /// (<see cref="HoursGate"/>): a run whose elements lost their brands, matching by location instead, can't pass for a
    /// good one.
    /// </summary>
    public bool Counts => MatchedBy != StationHoursEntry.ByLocation;
}

/// <param name="Matches">At most one per station.</param>
/// <param name="Unmatched">Records for one place (a natural key, a store number or a point) that found no station.</param>
/// <param name="Ambiguous">Of those, the ones left out because which station they're about couldn't be told.</param>
public sealed record MatchResult(IReadOnlyList<HoursMatch> Matches, int Unmatched, int Ambiguous)
{
    /// <summary>The matches that count (<see cref="HoursMatch.Counts"/>), those by location left out.</summary>
    public int Counted => Matches.Count(m => m.Counts);

    /// <summary>The matches by address (<see cref="StationHoursEntry.ByAddress"/>), for the logs.</summary>
    public int ByAddress => Matches.Count(m => m.MatchedBy == StationHoursEntry.ByAddress);

    /// <summary>The matches by location (<see cref="StationHoursEntry.ByLocation"/>), without hours, for the logs.</summary>
    public int ByLocation => Matches.Count(m => m.MatchedBy == StationHoursEntry.ByLocation);
}

/// <summary>
/// Gives each station at most one of a source's records, never guessing. A rule written for one station (by its natural
/// key) comes first, then Couche-Tard's stores by the number in the stations' names, then the records with a point (by
/// distance, then by address), then a brand's rule for the brand's stations left, and last, for OpenStreetMap, an element
/// of another brand that's on the station (by location).
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
/// <para>
/// A record with a point still free then matches a station still free of its own brand family whose house number and
/// postal code both agree with it, within <see cref="AddressReachM"/>: one of the two points is off, the brand's more
/// often than the feed's (Ultramar's locator puts some of its stations a street or a village away). A postal code alone
/// is never enough: a rural one covers villages kilometres apart. A record that agrees so with two stations, or a
/// station with two such records, matches neither.
/// </para>
/// <para>
/// Last, when the source says so (<see cref="IHoursSource.OtherBrandReachM"/>: OpenStreetMap's), a station still without
/// a record gets its nearest free one when it's within that reach and of another brand or none, no other feed station
/// (matched or not, of any brand) is within <see cref="LocationClearanceM"/> of it, and no second free record is within
/// the reach of the station. OpenStreetMap's brand is often years out of date (Nutrinor's stations are still Sonic's
/// there), and must not keep a station from the element that's on it. The match is never confirmed, and the record's
/// hours aren't taken (an out-of-date brand suggests out-of-date hours): it says which element to edit.
/// </para>
/// </summary>
public static partial class HoursMatcher
{
    /// <summary>
    /// How far a Costco record may be from its station, whatever the source: the feed's point is on the warehouse, and its
    /// gas bar can be 540 m away (Drummondville's).
    /// </summary>
    public const double CostcoReachM = 600;

    /// <summary>
    /// How far a record may be from a station of its brand with the same house number and postal code, both left free
    /// by the match by distance: one of the two points is off, by a street (Ultramar's locator) or a village (the feed).
    /// Further, the same number in the same rural postal code is as likely another road.
    /// </summary>
    public const double AddressReachM = 2_000;

    /// <summary>
    /// How far from a record matched by location (<see cref="IHoursSource.OtherBrandReachM"/>) any other feed station must
    /// be: the element could be that station's, its brand out of date or not.
    /// </summary>
    public const double LocationClearanceM = 150;

    private const double EarthRadiusM = 6_371_008.8;

    /// <param name="radiusM">How far a record may be from a station of its brand (<see cref="IHoursSource.MatchRadiusM"/>).</param>
    /// <param name="otherBrandReachM">
    /// How far a record of another brand, or none, may be from a station left without one (<see cref="IHoursSource.OtherBrandReachM"/>);
    /// null to match none so.
    /// </param>
    public static MatchResult Match(
        IReadOnlyList<SourceRecord> records, IReadOnlyList<MatchStation> stations, double radiusM, double? otherBrandReachM = null)
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

        var located = new Located(records
            .Where(r => r.NaturalKey is null && r.StoreNumber is null && r.Latitude is not null && r.Longitude is not null)
            .ToList());
        Take(MatchByDistance(located, Free(), radiusM));
        Take(MatchByAddress(located, Free()));

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

        if (otherBrandReachM is { } reach)
        {
            Take(MatchByLocation(located, stations, Free(), reach));
        }

        return new MatchResult(matches, unmatched + located.Unmatched, ambiguous + located.Ambiguous);

        List<MatchStation> Free() => [.. stations.Where(s => !taken.Contains(s.Id))];

        void Take(List<HoursMatch> found)
        {
            foreach (var match in found)
            {
                taken.Add(match.StationId);
                matches.Add(match);
            }
        }
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

    private static List<HoursMatch> MatchByDistance(Located located, List<MatchStation> stations, double radiusM)
    {
        var records = located.Records;
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
        var matchedStations = new HashSet<long>();
        var matches = new List<HoursMatch>();
        foreach (var pair in ordered)
        {
            if (located.Matched.Contains(pair.Record) || matchedStations.Contains(pair.StationId))
            {
                continue;
            }

            if (!pair.Confirmed && ordered.Any(other => IsRival(pair, other)))
            {
                located.LeftOut.Add(pair.Record);
                continue;
            }

            located.Matched.Add(pair.Record);
            matchedStations.Add(pair.StationId);
            matches.Add(new HoursMatch(pair.StationId, records[pair.Record], StationHoursEntry.ByDistance, (int)Math.Round(pair.DistanceM), pair.Confirmed));
        }

        return matches;

        bool IsRival(Pair pair, Pair other) =>
            other != pair && !located.Matched.Contains(other.Record) && !matchedStations.Contains(other.StationId)
            && (other.Record == pair.Record
                || other.StationId == pair.StationId && Disagree(records[other.Record], records[pair.Record]));
    }

    /// <summary>
    /// The records still free, each with the station still free of its brand, house number and postal code within
    /// <see cref="AddressReachM"/>; a record with two such stations, and a station with two such records, match none.
    /// </summary>
    private static List<HoursMatch> MatchByAddress(Located located, List<MatchStation> stations)
    {
        var pairs = new List<Pair>();
        for (var r = 0; r < located.Records.Count; r++)
        {
            var record = located.Records[r];
            if (located.Matched.Contains(r) || record.BrandFamily is null
                || record.HouseNumber is not { Length: > 0 } number || record.Postcode is not { Length: > 0 } postcode)
            {
                continue;
            }

            foreach (var station in stations)
            {
                if (station.BrandFamily == record.BrandFamily && station.HouseNumber == number && station.Postcode == postcode
                    && Distance(record, station) is { } distance && distance <= AddressReachM)
                {
                    pairs.Add(new Pair(r, station.Id, distance, Confirmed: true));
                }
            }
        }

        var perRecord = pairs.CountBy(p => p.Record).ToDictionary();
        var perStation = pairs.CountBy(p => p.StationId).ToDictionary();
        var matches = new List<HoursMatch>();
        foreach (var pair in pairs)
        {
            if (perRecord[pair.Record] > 1 || perStation[pair.StationId] > 1)
            {
                located.LeftOut.Add(pair.Record);
                continue;
            }

            located.Matched.Add(pair.Record);
            matches.Add(new HoursMatch(pair.StationId, located.Records[pair.Record], StationHoursEntry.ByAddress, (int)Math.Round(pair.DistanceM), Confirmed: true));
        }

        return matches;
    }

    /// <summary>
    /// Each station still free, with its one free record within <paramref name="reachM"/> when that record is of another
    /// brand or none and no other station is within <see cref="LocationClearanceM"/> of it; the record without its hours.
    /// A record taken so is within reach of no other station (one would be within the clearance), so the order the
    /// stations are taken in doesn't matter.
    /// </summary>
    private static List<HoursMatch> MatchByLocation(Located located, IReadOnlyList<MatchStation> stations, List<MatchStation> free, double reachM)
    {
        var placed = stations.Where(s => s.Latitude is not null && s.Longitude is not null).ToList();
        var matches = new List<HoursMatch>();
        foreach (var station in free)
        {
            if (station is not { Latitude: { } latitude, Longitude: { } longitude })
            {
                continue;
            }

            var near = new List<(int Record, double DistanceM)>();
            for (var r = 0; r < located.Records.Count; r++)
            {
                var record = located.Records[r];
                if (!located.Matched.Contains(r) && DistanceM(record.Latitude!.Value, record.Longitude!.Value, latitude, longitude) is var distance
                    && distance <= reachM)
                {
                    near.Add((r, distance));
                }
            }

            if (near.Count > 1)
            {
                // Which of them is the station's can't be told.
                located.LeftOut.UnionWith(near.Select(n => n.Record));
                continue;
            }

            if (near is not [var (index, metres)])
            {
                continue;
            }

            // One of the station's own brand was left out by distance, for a rival: it's no more the station's now.
            var element = located.Records[index];
            if (element.BrandFamily is not null && element.BrandFamily == station.BrandFamily)
            {
                continue;
            }

            if (placed.Any(other => other.Id != station.Id
                && DistanceM(element.Latitude!.Value, element.Longitude!.Value, other.Latitude!.Value, other.Longitude!.Value) <= LocationClearanceM))
            {
                continue;
            }

            located.Matched.Add(index);
            matches.Add(new HoursMatch(
                station.Id,
                element with { Rule = null, CheckedAt = null, TemporarilyClosed = false },
                StationHoursEntry.ByLocation,
                (int)Math.Round(metres),
                Confirmed: false));
        }

        return matches;
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

    /// <summary>The records with a point, and which of them each pass matched or left out, by index.</summary>
    private sealed class Located(List<SourceRecord> records)
    {
        public List<SourceRecord> Records { get; } = records;

        public HashSet<int> Matched { get; } = [];

        /// <summary>Left out because which station they're about couldn't be told: ambiguous, unless a later pass matches them.</summary>
        public HashSet<int> LeftOut { get; } = [];

        public int Unmatched => Records.Count - Matched.Count;

        public int Ambiguous => LeftOut.Count(r => !Matched.Contains(r));
    }
}
