using System.Text;
using Essence.Data;
using Essence.Data.Entities;
using Essence.Ingest.Hours;
using Essence.Ranking;

namespace Essence.Ingest.Positions;

/// <summary>A feed station, as finding where it is reads it.</summary>
/// <param name="BrandFamily">Its brand, folded (<see cref="Hours.BrandFamily.Of"/>); null when it has none.</param>
/// <param name="Latitude">The feed's point.</param>
/// <param name="Longitude">The feed's point.</param>
/// <param name="HouseNumber">Its address's number (<see cref="HoursMatcher.HouseNumber"/>).</param>
/// <param name="Postcode">Its postal code, folded: <c>G0V1S0</c>.</param>
/// <param name="Address">As the feed writes it: "1918 boul. Tadoussac, Saint-Fulgence".</param>
public sealed record PositionStation(
    long Id,
    string NaturalKey,
    string? BrandFamily,
    double Latitude,
    double Longitude,
    string? HouseNumber,
    string Postcode,
    string Address)
{
    /// <summary>Couche-Tard's store number in its name (<see cref="HoursMatcher.StoreNumber"/>).</summary>
    public int? StoreNumber { get; init; }
}

/// <summary>A source's records, as their last download kept on disk has them (<see cref="HoursCache"/>).</summary>
/// <param name="Name">The source's: <c>osm</c>, <c>ultramar</c>…</param>
/// <param name="IsOsm">OpenStreetMap's elements; otherwise a brand's locator.</param>
public sealed record PositionSource(string Name, bool IsOsm, IReadOnlyList<SourceRecord> Records);

/// <summary>A station the feed may put in the wrong place: a row of <c>position_candidates</c>.</summary>
/// <param name="Latitude">The point proposed: the mean of <paramref name="Evidence"/>.</param>
/// <param name="Longitude">The point proposed.</param>
/// <param name="DistanceM">From the feed's point to the proposed one.</param>
/// <param name="Evidence">The positions that agree, of two kinds or three.</param>
public sealed record FoundPosition(
    PositionStation Station, double Latitude, double Longitude, int DistanceM, IReadOnlyList<PositionEvidence> Evidence);

/// <summary>
/// Finds the stations whose point in the feed may be wrong, from independent kinds of position: OpenStreetMap's fuel
/// element at the station's address (of its brand or none: <see cref="PositionCandidate.FromOsm"/>), a brand's locator
/// (the store with the number in the station's name, or one of its brand at its address: <see cref="PositionCandidate.FromLocator"/>),
/// and Photon's answer for the station's address (a place of OpenStreetMap's at that address, never a fuel element:
/// <see cref="PositionCandidate.FromPhoton"/>). An address is the house number <b>and</b> the postal code, within
/// <see cref="SearchReachM"/>: a postal code alone covers villages in the country. A station is a candidate when two
/// positions of different kinds agree within <see cref="AgreeWithinM"/>, both more than <see cref="FarFromFeedM"/> from
/// the feed's point, and nothing is at the feed's point: no position of the station, no OpenStreetMap element and no
/// locator record of its brand within <see cref="AtFeedPointM"/>. Single sources are often wrong (Ultramar's locator in 10
/// of the 12 stations it put far off, Circle K's in 9 of 10, Photon in 28 of 46), so one never makes a candidate. Pure.
/// </summary>
public sealed class PositionFinder
{
    /// <summary>How far from the feed's point a position at the station's address may be: past it, it's another road of the same name.</summary>
    public const double SearchReachM = 30_000;

    /// <summary>Two positions this close to each other agree: one station's two sources, an element and an address node.</summary>
    public const double AgreeWithinM = 75;

    /// <summary>Positions this close to the feed's point or closer agree with it, about: no reason to move it.</summary>
    public const double FarFromFeedM = 150;

    /// <summary>Something of the station's this close to the feed's point says the point may be right.</summary>
    public const double AtFeedPointM = 75;

    /// <summary>Photon's answers read for an address.</summary>
    public const int GeocodeLimit = 10;

    /// <summary>The feed's abbreviations of street types, spelled out for Photon, which knows the words.</summary>
    private static readonly Dictionary<string, string> StreetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["boul."] = "boulevard",
        ["boul"] = "boulevard",
        ["ch."] = "chemin",
        ["che."] = "chemin",
        ["rte"] = "route",
        ["rte."] = "route",
        ["av."] = "avenue",
        ["ave."] = "avenue",
        ["aut."] = "autoroute",
    };

    private readonly List<Located> _osm = [];
    private readonly List<Located> _locators = [];
    private readonly ILookup<(string HouseNumber, string Postcode), Located> _byAddress;
    private readonly ILookup<int, Located> _byStoreNumber;
    private readonly HashSet<int> _storeNumbersOfOneStation;
    private readonly HashSet<string> _osmElements;

    /// <param name="stations">Every active station with a point: a store number two of them carry joins neither.</param>
    public PositionFinder(IReadOnlyList<PositionStation> stations, IReadOnlyList<PositionSource> sources)
    {
        foreach (var source in sources)
        {
            foreach (var record in source.Records)
            {
                if (record is { Latitude: { } latitude, Longitude: { } longitude })
                {
                    (source.IsOsm ? _osm : _locators).Add(new Located(source, record, latitude, longitude));
                }
            }
        }

        _byAddress = _osm.Concat(_locators)
            .Where(r => r.Record is { HouseNumber.Length: > 0, Postcode.Length: > 0 })
            .ToLookup(r => (r.Record.HouseNumber!, r.Record.Postcode!));
        _byStoreNumber = _locators.Where(r => r.Record.StoreNumber is not null).ToLookup(r => r.Record.StoreNumber!.Value);
        _storeNumbersOfOneStation = stations
            .Where(s => s.StoreNumber is not null)
            .CountBy(s => s.StoreNumber!.Value)
            .Where(c => c.Value == 1)
            .Select(c => c.Key)
            .ToHashSet();
        _osmElements = _osm.Select(r => r.Record.Ref).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The station's positions from OpenStreetMap and the brands' locators: elements of its brand or none at its address,
    /// the store with the number in its name, and stores of its brand at its address, within <see cref="SearchReachM"/>.
    /// </summary>
    public IReadOnlyList<PositionEvidence> Known(PositionStation station)
    {
        var found = new List<PositionEvidence>();
        if (station is { HouseNumber.Length: > 0, Postcode.Length: > 0 })
        {
            foreach (var located in _byAddress[(station.HouseNumber, station.Postcode)])
            {
                var family = located.Record.BrandFamily;
                var ofTheBrand = located.Source.IsOsm
                    ? family is null || family == station.BrandFamily
                    : family is not null && family == station.BrandFamily;
                if (ofTheBrand)
                {
                    Add(located);
                }
            }
        }

        if (station.StoreNumber is { } number && _storeNumbersOfOneStation.Contains(number))
        {
            foreach (var located in _byStoreNumber[number])
            {
                Add(located);
            }
        }

        return found;

        void Add(Located located)
        {
            var kind = located.Source.IsOsm ? PositionCandidate.FromOsm : PositionCandidate.FromLocator;
            var reference = located.Source.IsOsm ? located.Record.Ref : $"{located.Source.Name}/{located.Record.Ref}";
            if (Evidence(station, kind, reference, located.Latitude, located.Longitude) is { } evidence
                && !found.Any(f => f.Kind == kind && f.Ref == reference))
            {
                found.Add(evidence);
            }
        }
    }

    /// <summary>
    /// Whether the feed's point may be right: a position of the station, or an OpenStreetMap element or a locator record
    /// of its brand, is within <see cref="AtFeedPointM"/> of it.
    /// </summary>
    public bool AtTheFeedPoint(PositionStation station, IReadOnlyList<PositionEvidence> positions) =>
        positions.Any(p => p.DistanceM <= AtFeedPointM)
        || station.BrandFamily is { } family
        && _osm.Concat(_locators).Any(r => r.Record.BrandFamily == family && DistanceM(station, r.Latitude, r.Longitude) <= AtFeedPointM);

    /// <summary>
    /// Whether to ask Photon for the station's address: a position of another kind is more than <see cref="FarFromFeedM"/>
    /// away, and nothing is at the feed's point. Others can't become candidates, so they cost no request.
    /// </summary>
    public bool NeedsGeocode(PositionStation station, IReadOnlyList<PositionEvidence> known) =>
        known.Any(p => p.DistanceM > FarFromFeedM) && !AtTheFeedPoint(station, known);

    /// <summary>
    /// Photon's answers that are the station's address, as positions: a place of OpenStreetMap's at a house's level (an
    /// address, a building, the station's shop or restaurant), never an interpolated number (a street's range, not a
    /// house) nor a fuel element, which is OpenStreetMap's kind already; with the station's house number, and its postal
    /// code or the town its address names (an address's postal code is often another on OpenStreetMap); within
    /// <see cref="SearchReachM"/>. A wrong one is harmless alone: it must still agree with another kind's.
    /// </summary>
    public IReadOnlyList<PositionEvidence> FromPhoton(PositionStation station, IReadOnlyList<PhotonPlace> places)
    {
        var town = Town(station.Address);
        var found = new List<PositionEvidence>();
        foreach (var place in places)
        {
            var isHouse = place.Type == "house" && place is not { OsmKey: "place", OsmValue: "house_number" }
                && place is not { OsmKey: "amenity", OsmValue: "fuel" };
            var atTheAddress = station.HouseNumber is { Length: > 0 } number && HoursMatcher.HouseNumber(place.HouseNumber) == number
                && (station.Postcode.Length > 0 && StationKey.NormalizePostalCode(place.Postcode) == station.Postcode
                    || town is not null && (Town(place.City) == town || Town(place.Locality) == town || Town(place.District) == town));
            if (isHouse && atTheAddress && place.OsmRef is { } reference && !_osmElements.Contains(reference)
                && !found.Any(f => f.Ref == reference)
                && Evidence(station, PositionCandidate.FromPhoton, reference, place.Location.Latitude, place.Location.Longitude) is { } evidence)
            {
                found.Add(evidence);
            }
        }

        return found;
    }

    /// <summary>
    /// The station as a candidate, or null: the two positions of different kinds that agree best (within
    /// <see cref="AgreeWithinM"/>), both more than <see cref="FarFromFeedM"/> from the feed's point, with the closest of a
    /// third kind that agrees with both, and nothing at the feed's point. The point proposed is their mean, itself more
    /// than <see cref="FarFromFeedM"/> away.
    /// </summary>
    public FoundPosition? Candidate(PositionStation station, IReadOnlyList<PositionEvidence> positions)
    {
        var far = positions.Where(p => p.DistanceM > FarFromFeedM).ToList();
        (PositionEvidence A, PositionEvidence B, double DistanceM)? best = null;
        for (var i = 0; i < far.Count; i++)
        {
            for (var j = i + 1; j < far.Count; j++)
            {
                if (far[i].Kind != far[j].Kind && Between(far[i], far[j]) is var apart && apart <= AgreeWithinM && !(best?.DistanceM <= apart))
                {
                    best = (far[i], far[j], apart);
                }
            }
        }

        // The pair first: the feed's point is checked only for the few stations that have one.
        if (best is not { } pair || AtTheFeedPoint(station, positions))
        {
            return null;
        }

        var (a, b, _) = pair;
        List<PositionEvidence> agree = [a, b];
        var third = far
            .Where(p => p.Kind != a.Kind && p.Kind != b.Kind && Between(p, a) <= AgreeWithinM && Between(p, b) <= AgreeWithinM)
            .MinBy(p => Between(p, a) + Between(p, b));
        if (third is not null)
        {
            agree.Add(third);
        }

        var latitude = Math.Round(agree.Average(p => p.Latitude), 6);
        var longitude = Math.Round(agree.Average(p => p.Longitude), 6);
        var distance = DistanceM(station, latitude, longitude);
        return distance > FarFromFeedM
            ? new FoundPosition(station, latitude, longitude, (int)Math.Round(distance), [.. agree.OrderBy(p => KindOrder(p.Kind)).ThenBy(p => p.Ref, StringComparer.Ordinal)])
            : null;
    }

    /// <summary>
    /// What Photon is asked for a station's address: the feed's, its street types spelled out ("1918 boul. Tadoussac,
    /// Saint-Fulgence" is "1918 boulevard Tadoussac, Saint-Fulgence").
    /// </summary>
    public static string GeocodeQuery(string address) =>
        string.Join(' ', StationKey.CollapseWhitespace(address).Split(' ').Select(w => StreetTypes.GetValueOrDefault(w, w)));

    /// <summary>
    /// A town's name, folded to compare Photon's with the feed's: "Saint-Fulgence", "ST-FULGENCE" and "Saint Fulgence" are
    /// <c>saint-fulgence</c>. For an address, the town after its last comma; null without one.
    /// </summary>
    public static string? Town(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var comma = text.LastIndexOf(',');
        var name = StationKey.Fold(comma >= 0 ? text[(comma + 1)..] : text);
        var builder = new StringBuilder(name.Length);
        var pendingDash = false;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(c);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var folded = builder.ToString();
        folded = folded.StartsWith("st-", StringComparison.Ordinal) ? "saint-" + folded[3..]
            : folded.StartsWith("ste-", StringComparison.Ordinal) ? "sainte-" + folded[4..]
            : folded;
        return folded.Length > 0 ? folded : null;
    }

    private static PositionEvidence? Evidence(PositionStation station, string kind, string reference, double latitude, double longitude) =>
        DistanceM(station, latitude, longitude) is var distance && distance <= SearchReachM
            ? new PositionEvidence(kind, reference, latitude, longitude, (int)Math.Round(distance))
            : null;

    private static double DistanceM(PositionStation station, double latitude, double longitude) =>
        PositionCorrections.DistanceM(station.Latitude, station.Longitude, latitude, longitude);

    private static double Between(PositionEvidence a, PositionEvidence b) =>
        PositionCorrections.DistanceM(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    private static int KindOrder(string kind) => kind switch
    {
        PositionCandidate.FromOsm => 0,
        PositionCandidate.FromLocator => 1,
        _ => 2,
    };

    private sealed record Located(PositionSource Source, SourceRecord Record, double Latitude, double Longitude);
}
