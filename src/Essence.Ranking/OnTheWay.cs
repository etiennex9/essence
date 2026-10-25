namespace Essence.Ranking;

/// <summary>A priced station near a route, before it's measured by road.</summary>
/// <param name="OffKm">From the station to the route, straight.</param>
/// <param name="KmAlong">How far along the route it is.</param>
public sealed record RouteStation(long Id, decimal PriceCents, double KmAlong, double OffKm);

/// <summary>A station on the way, measured by road.</summary>
/// <param name="ToKm">From the start to the station, by road.</param>
/// <param name="OnwardKm">From the station to the destination, by road.</param>
public sealed record TripCandidate(
    long Id,
    decimal PriceCents,
    double KmAlong,
    double ToKm,
    double ToMinutes,
    double OnwardKm,
    double OnwardMinutes);

/// <summary>The road distances of a trip: start to destination, and through each station.</summary>
/// <param name="DirectKm">From the start to the destination, measured with the same snapping as the stations.</param>
public sealed record TripRoads(double DirectKm, double DirectMinutes, IReadOnlyList<TripCandidate> Candidates);

/// <param name="DetourKm">What stopping there adds to the trip; 0 when it's on the way.</param>
/// <param name="Cost">The fill-up with the gas the detour burns (<see cref="Costs.Fill"/>).</param>
/// <param name="Saves">The reference's cost minus this one's; negative when it costs more.</param>
/// <param name="Reachable">The car's range gets there (always, when the range isn't counted).</param>
/// <param name="OverDetourLimit">Its detour takes longer than the longest detour: listed, never picked.</param>
public sealed record RankedTripCandidate(
    TripCandidate Candidate,
    double DetourKm,
    double DetourMinutes,
    decimal Cost,
    decimal Saves,
    bool Reachable,
    bool OverDetourLimit)
{
    public long Id => Candidate.Id;

    /// <summary>Stopping there adds next to nothing: at most <see cref="OnTheWay.NoDetourKm"/> and <see cref="OnTheWay.NoDetourMinutes"/>.</summary>
    public bool IsOnTheWay => DetourKm <= OnTheWay.NoDetourKm && DetourMinutes <= OnTheWay.NoDetourMinutes;
}

/// <param name="Ranked">Every candidate: the reachable ones lowest cost first (ties: detour minutes, price, id), then the others.</param>
/// <param name="Reference">
/// What the savings are counted against: the cheapest reachable station on the way, or with none on the way, the one with
/// the smallest detour. Null when no candidate is reachable.
/// </param>
/// <param name="ReferenceOnTheWay">The reference is on the way; otherwise it's the smallest detour.</param>
/// <param name="Pick">The reachable station within the longest detour with the lowest cost, or the reference when that one saves less than <see cref="NearYou.SmallestSaving"/>.</param>
/// <param name="BiggestSaving">The reachable station with the lowest cost, past the longest detour included; null when it's the pick or saves too little.</param>
/// <param name="StopBeforeKm">
/// Where the range runs out, less the spare: null when it isn't counted or the trip is shorter (no stop needed), and 0 with
/// <paramref name="LowFuel"/>.
/// </param>
/// <param name="LowFuel">
/// The range less the spare reaches no station: reachable then means within the whole range, or failing that, the
/// station that's nearest by road.
/// </param>
/// <param name="LongTrip">At least <see cref="OnTheWay.LongTripKm"/>: the range counts.</param>
public sealed record TripRanking(
    IReadOnlyList<RankedTripCandidate> Ranked,
    RankedTripCandidate? Reference,
    bool ReferenceOnTheWay,
    RankedTripCandidate? Pick,
    RankedTripCandidate? BiggestSaving,
    double? StopBeforeKm,
    bool LowFuel,
    bool LongTrip);

/// <summary>The range the driver read on the dashboard, and how much of it is never counted on.</summary>
public sealed record FuelRange(double RangeKm, double SpareKm);

/// <summary>Best on the way: where to fill up on a trip, counting the gas the detour burns, within the car's range.</summary>
public static class OnTheWay
{
    /// <summary>A stop that adds at most this much is on the way.</summary>
    public const double NoDetourKm = 1.0;

    /// <inheritdoc cref="NoDetourKm"/>
    public const double NoDetourMinutes = 2.0;

    /// <summary>Trips shorter than this don't count the range: a car rarely sets out with less.</summary>
    public const double LongTripKm = 100;

    public const double MinHalfWidthKm = 1;
    public const double MaxHalfWidthKm = 8;

    /// <summary>At most this many stations are measured by road (two OSRM table calls).</summary>
    public const int MaxCandidates = 100;

    /// <summary>Candidates are chosen per stretch of this many km, so a long route is covered from start to end.</summary>
    public const double BinKm = 10;

    public const int ClosestPerBin = 2;
    public const int CheapestPerBin = 3;

    /// <summary>
    /// Stations that come first along the route are kept whatever the range, so a car low on fuel still has the nearest.
    /// </summary>
    public const int FirstAlwaysKept = 5;

    /// <summary>The trip's start and destination must be within this of a road.</summary>
    public const double MaxEndSnapMeters = 1000;

    /// <summary>
    /// How far off the route stations are looked for: a station x km off it costs at least 2x km of driving, so half a km
    /// per minute of the longest detour, between <see cref="MinHalfWidthKm"/> and <see cref="MaxHalfWidthKm"/>.
    /// </summary>
    public static double HalfWidthKm(double maxDetourMinutes) => Math.Clamp(maxDetourMinutes * 0.5, MinHalfWidthKm, MaxHalfWidthKm);

    /// <summary>
    /// The stations worth measuring by road, at most <see cref="MaxCandidates"/>, in order along the route. Past
    /// <paramref name="maxKmAlong"/> (the range), only the <see cref="FirstAlwaysKept"/> first are kept. Then, in each
    /// <see cref="BinKm"/> stretch, the <see cref="ClosestPerBin"/> closest to the route and the <see cref="CheapestPerBin"/>
    /// cheapest, taken a round at a time across the stretches so every stretch gets its first ones; then the cheapest of
    /// the rest.
    /// </summary>
    public static List<RouteStation> SelectCandidates(IReadOnlyCollection<RouteStation> stations, double? maxKmAlong)
    {
        var alongFirst = stations.OrderBy(s => s.KmAlong).ThenBy(s => s.Id).ToList();
        var inRange = maxKmAlong is { } limit
            ? alongFirst.Where((s, i) => i < FirstAlwaysKept || s.KmAlong <= limit).ToList()
            : alongFirst;

        var bins = inRange
            .GroupBy(s => (int)Math.Floor(s.KmAlong / BinKm))
            .OrderBy(g => g.Key)
            .Select(g => (
                Closest: g.OrderBy(s => s.OffKm).ThenBy(s => s.Id).Take(ClosestPerBin).ToList(),
                Cheapest: g.OrderBy(s => s.PriceCents).ThenBy(s => s.OffKm).ThenBy(s => s.Id).Take(CheapestPerBin).ToList()))
            .ToList();

        var selected = new HashSet<long>();
        var chosen = new List<RouteStation>();
        void Add(RouteStation station)
        {
            if (chosen.Count < MaxCandidates && selected.Add(station.Id))
            {
                chosen.Add(station);
            }
        }

        // Rounds: the closest, the cheapest, the second closest, the second and third cheapest.
        (bool Closest, int Rank)[] rounds = [(true, 0), (false, 0), (true, 1), (false, 1), (false, 2)];
        foreach (var (closest, rank) in rounds)
        {
            foreach (var bin in bins)
            {
                var list = closest ? bin.Closest : bin.Cheapest;
                if (rank < list.Count)
                {
                    Add(list[rank]);
                }
            }
        }

        foreach (var station in inRange.OrderBy(s => s.PriceCents).ThenBy(s => s.OffKm).ThenBy(s => s.Id))
        {
            Add(station);
        }

        return [.. chosen.OrderBy(s => s.KmAlong).ThenBy(s => s.Id)];
    }

    /// <summary>
    /// The trip's road distances from two tables on the same points (start, destination, stations): <paramref name="fromStart"/>
    /// from the start (its one source) to the destination then each station, and <paramref name="toDestination"/> from each
    /// station to the destination. A station with no road either way, or that snaps more than
    /// <see cref="NearYou.MaxStationSnapMeters"/> from its road, is left out. Null when there's no road to the destination.
    /// </summary>
    /// <param name="stations">The stations, in the tables' order.</param>
    public static TripRoads? ByRoad(IReadOnlyList<RouteStation> stations, OsrmTable fromStart, OsrmTable toDestination)
    {
        if (fromStart.DistancesMeters[0][0] is not { } directMeters || fromStart.DurationsSeconds[0][0] is not { } directSeconds)
        {
            return null;
        }

        var candidates = new List<TripCandidate>(stations.Count);
        for (var i = 0; i < stations.Count; i++)
        {
            if (fromStart.Destinations[i + 1].SnapMeters > NearYou.MaxStationSnapMeters
                || fromStart.DistancesMeters[0][i + 1] is not { } toMeters || fromStart.DurationsSeconds[0][i + 1] is not { } toSeconds
                || toDestination.DistancesMeters[i][0] is not { } onwardMeters || toDestination.DurationsSeconds[i][0] is not { } onwardSeconds)
            {
                continue;
            }

            var station = stations[i];
            candidates.Add(new TripCandidate(
                station.Id, station.PriceCents, station.KmAlong, toMeters / 1000, toSeconds / 60, onwardMeters / 1000, onwardSeconds / 60));
        }

        return new TripRoads(directMeters / 1000, directSeconds / 60, candidates);
    }

    /// <summary>Ranks the stations on the way for a car.</summary>
    /// <param name="tripKm">The route's length: under <see cref="LongTripKm"/>, the range isn't counted.</param>
    /// <param name="maxDetourMinutes">The longest detour: a station whose detour takes longer is listed, not picked.</param>
    /// <param name="range">What the dashboard says is left; null when the driver didn't say.</param>
    public static TripRanking Rank(TripRoads roads, double tripKm, Car car, double maxDetourMinutes, FuelRange? range)
    {
        var longTrip = tripKm >= LongTripKm;
        var measured = roads.Candidates
            .Select(c => (
                Candidate: c,
                DetourKm: Math.Max(0, c.ToKm + c.OnwardKm - roads.DirectKm),
                DetourMinutes: Math.Max(0, c.ToMinutes + c.OnwardMinutes - roads.DirectMinutes)))
            .ToList();

        // Which stations the fuel left reaches, by road from the start.
        double? stopBeforeKm = null;
        var lowFuel = false;
        Func<TripCandidate, bool> reachable = _ => true;
        if (longTrip && range is { } fuel)
        {
            var usableKm = fuel.RangeKm - fuel.SpareKm;
            if (usableKm > 0 && measured.Any(m => m.Candidate.ToKm <= usableKm))
            {
                reachable = c => c.ToKm <= usableKm;
                stopBeforeKm = tripKm > usableKm ? usableKm : null;
            }
            else if (measured.Count > 0)
            {
                lowFuel = true;
                stopBeforeKm = 0;
                var nearest = measured.MinBy(m => (m.Candidate.ToKm, m.Candidate.Id)).Candidate;
                reachable = measured.Any(m => m.Candidate.ToKm <= fuel.RangeKm)
                    ? c => c.ToKm <= fuel.RangeKm
                    : c => c.Id == nearest.Id;
            }
        }

        var reachableOnes = measured.Where(m => reachable(m.Candidate)).ToList();
        var onTheWay = reachableOnes.Where(m => m.DetourKm <= NoDetourKm && m.DetourMinutes <= NoDetourMinutes).ToList();
        var referenceOnTheWay = onTheWay.Count > 0;

        // The reference's cost includes its own (small) detour; with none on the way, the smallest detour is the reference.
        var referenceMeasure = referenceOnTheWay
            ? onTheWay.OrderBy(m => m.Candidate.PriceCents).ThenBy(m => Costs.Fill(m.Candidate.PriceCents, car, m.DetourKm))
                .ThenBy(m => m.DetourMinutes).ThenBy(m => m.Candidate.Id).First()
            : reachableOnes.OrderBy(m => m.DetourMinutes).ThenBy(m => Costs.Fill(m.Candidate.PriceCents, car, m.DetourKm))
                .ThenBy(m => m.Candidate.PriceCents).ThenBy(m => m.Candidate.Id).FirstOrDefault();
        var referenceCost = referenceMeasure.Candidate is { } r ? Costs.Fill(r.PriceCents, car, referenceMeasure.DetourKm) : 0;

        var ranked = measured
            .Select(m =>
            {
                var cost = Costs.Fill(m.Candidate.PriceCents, car, m.DetourKm);
                var isReference = m.Candidate.Id == referenceMeasure.Candidate?.Id;
                return new RankedTripCandidate(
                    m.Candidate, m.DetourKm, m.DetourMinutes, cost, referenceCost - cost, reachable(m.Candidate),
                    !isReference && m.DetourMinutes > maxDetourMinutes);
            })
            .OrderBy(r => !r.Reachable)
            .ThenBy(r => r.Cost)
            .ThenBy(r => r.DetourMinutes)
            .ThenBy(r => r.Candidate.PriceCents)
            .ThenBy(r => r.Id)
            .ToList();

        var reference = ranked.FirstOrDefault(r => r.Id == referenceMeasure.Candidate?.Id);
        RankedTripCandidate? pick = null, biggestSaving = null;
        if (reference is not null)
        {
            // The reference is always pickable, so there's a best; a saving too small to be worth the detour leaves the reference.
            var best = ranked.First(r => r.Reachable && !r.OverDetourLimit);
            pick = best.Saves >= NearYou.SmallestSaving ? best : reference;
            var cheapestOverall = ranked.First(r => r.Reachable);
            biggestSaving = cheapestOverall.Id != pick.Id && cheapestOverall.Saves >= NearYou.SmallestSaving ? cheapestOverall : null;
        }

        return new TripRanking(ranked, reference, referenceOnTheWay, pick, biggestSaving, stopBeforeKm, lowFuel, longTrip);
    }
}
