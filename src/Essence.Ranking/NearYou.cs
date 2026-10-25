namespace Essence.Ranking;

/// <summary>A priced station the driver could fill up at, and what going there takes.</summary>
/// <param name="DistanceKm">From the driver to the station, one way.</param>
/// <param name="ExtraKm">What going there adds to the drive: near you, there and back.</param>
/// <param name="ExtraMinutes">How long driving <paramref name="ExtraKm"/> takes.</param>
/// <param name="DurationMinutes">From the driver to the station, one way, by road; null when estimated.</param>
public sealed record Candidate(long Id, decimal PriceCents, double DistanceKm, double ExtraKm, double ExtraMinutes, double? DurationMinutes = null);

/// <param name="Cost">The fill-up with the driving it takes (<see cref="Costs.Fill"/>).</param>
/// <param name="Saves">The reference's cost minus this one's; negative when it costs more.</param>
/// <param name="OverDetourLimit">Its drive takes more than the longest detour longer than the reference's: listed, never picked.</param>
/// <param name="SavingUncertain">
/// Distances are estimated, and with roads twice as long as the straight line (a river between) it would no longer save
/// <see cref="NearYou.SmallestSaving"/>: listed, never picked.
/// </param>
public sealed record RankedCandidate(Candidate Candidate, decimal Cost, decimal Saves, bool OverDetourLimit, bool SavingUncertain)
{
    public long Id => Candidate.Id;

    /// <summary>Can be the pick: within the longest detour, and sure to save.</summary>
    public bool Pickable => !OverDetourLimit && !SavingUncertain;
}

/// <param name="Ranked">Every candidate, lowest cost first (ties: distance, price, id).</param>
/// <param name="Reference">The closest station, which the savings are counted against.</param>
/// <param name="Pick">The pickable station with the lowest cost, or the reference when that one saves less than <see cref="NearYou.SmallestSaving"/>.</param>
/// <param name="Cheapest">The lowest price (ties: cost, distance, id).</param>
/// <param name="Listed">
/// The stations worth showing, lowest cost first: the pick, the reference, the cheapest, and the first
/// <see cref="NearYou.ListedPerGroup"/> others that save among the pickable ones and among the rest.
/// </param>
public sealed record NearbyRanking(
    IReadOnlyList<RankedCandidate> Ranked,
    RankedCandidate Reference,
    RankedCandidate Pick,
    RankedCandidate Cheapest,
    IReadOnlyList<RankedCandidate> Listed);

/// <summary>Best near you: where to fill up around the driver, counting the gas burned getting there and back.</summary>
public static class NearYou
{
    /// <summary>Roads are taken to be this much longer than the straight line, until distances come by road.</summary>
    public const double RoadFactor = 1.3;

    /// <summary>
    /// While distances are estimated, a station other than the closest is picked only if it still saves with roads this
    /// much longer than the straight line: crossing the St. Lawrence or the Rivière des Prairies can make them 2–4 times
    /// longer, which would turn a saving into a loss.
    /// </summary>
    public const double CautiousRoadFactor = 2.0;

    /// <summary>The average speed estimated distances are driven at, in town.</summary>
    public const double EstimatedSpeedKmh = 40;

    /// <summary>Savings under this are "about the same", and don't make a farther station the pick.</summary>
    public const decimal SmallestSaving = 0.25m;

    /// <summary>How many other stations that save are listed, among the pickable ones and among the rest.</summary>
    public const int ListedPerGroup = 4;

    /// <summary>At most this many stations are measured by road: OSRM's table is limited to 200 (osrm-routed's --max-table-size).</summary>
    public const int MaxByRoad = 200;

    /// <summary>The nearest stations measured by road whatever their price, so the closest by road is among them.</summary>
    public const int NearestByRoad = 10;

    /// <summary>
    /// Ranking by road needs the driver within this of a road; farther (a lake, the woods, past the border, where the
    /// data has no road), the distances are estimated.
    /// </summary>
    public const double MaxDriverSnapMeters = 1000;

    /// <summary>
    /// A candidate whose distance is estimated from the straight line (× <see cref="RoadFactor"/>), there and back, at
    /// <see cref="EstimatedSpeedKmh"/>.
    /// </summary>
    public static Candidate Estimate(long id, decimal priceCents, double straightKm)
    {
        var oneWayKm = straightKm * RoadFactor;
        var extraKm = 2 * oneWayKm;
        return new Candidate(id, priceCents, oneWayKm, extraKm, extraKm / EstimatedSpeedKmh * 60);
    }

    /// <summary>
    /// The stations worth measuring by road, at most <see cref="MaxByRoad"/>: the <see cref="NearestByRoad"/> nearest, then
    /// every one cheaper than the nearest, nearest first. A station that costs more than the nearest and is farther can't
    /// be the pick.
    /// </summary>
    public static List<T> SelectByRoad<T>(IReadOnlyCollection<T> stations, Func<T, double> straightKm, Func<T, decimal> priceCents)
    {
        var nearestFirst = stations.OrderBy(straightKm).ToList();
        if (nearestFirst.Count == 0)
        {
            return nearestFirst;
        }

        var nearestPrice = priceCents(nearestFirst[0]);
        return [.. nearestFirst.Take(NearestByRoad)
            .Concat(nearestFirst.Skip(NearestByRoad).Where(s => priceCents(s) < nearestPrice))
            .Take(MaxByRoad)];
    }

    /// <summary>
    /// Candidates by road from two tables on the same points: <paramref name="outbound"/> from the driver (its one source)
    /// to each station, and <paramref name="back"/> from each station to the driver. A station with no road either way is
    /// left out.
    /// </summary>
    /// <param name="stations">The stations, in the tables' order.</param>
    public static List<Candidate> ByRoad(IReadOnlyList<(long Id, decimal PriceCents)> stations, OsrmTable outbound, OsrmTable back)
    {
        var candidates = new List<Candidate>(stations.Count);
        for (var i = 0; i < stations.Count; i++)
        {
            if (outbound.DistancesMeters[0][i] is not { } thereMeters || outbound.DurationsSeconds[0][i] is not { } thereSeconds
                || back.DistancesMeters[i][0] is not { } backMeters || back.DurationsSeconds[i][0] is not { } backSeconds)
            {
                continue;
            }

            candidates.Add(new Candidate(
                stations[i].Id, stations[i].PriceCents, thereMeters / 1000, (thereMeters + backMeters) / 1000, (thereSeconds + backSeconds) / 60,
                thereSeconds / 60));
        }

        return candidates;
    }

    /// <summary>Ranks the candidates (each id once) for a car; null when there are none.</summary>
    /// <param name="maxDetourMinutes">The longest detour: how much longer than the closest's a station's drive may take to be picked.</param>
    /// <param name="byRoad">The distances are by road, not <see cref="Estimate"/>d, so the cautious check isn't needed.</param>
    public static NearbyRanking? Rank(IReadOnlyList<Candidate> candidates, Car car, double maxDetourMinutes, bool byRoad)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var closest = candidates.OrderBy(c => c.DistanceKm).ThenBy(c => c.Id).First();
        var closestCost = Costs.Fill(closest.PriceCents, car, closest.ExtraKm);
        var cautiousClosestCost = Costs.Fill(closest.PriceCents, car, Cautious(closest.ExtraKm));

        var ranked = candidates
            .Select(c =>
            {
                var cost = Costs.Fill(c.PriceCents, car, c.ExtraKm);
                var saves = closestCost - cost;
                var isClosest = c.Id == closest.Id;
                var overDetourLimit = !isClosest && c.ExtraMinutes - closest.ExtraMinutes > maxDetourMinutes;
                var savingUncertain = !isClosest && !byRoad && saves >= SmallestSaving
                    && cautiousClosestCost - Costs.Fill(c.PriceCents, car, Cautious(c.ExtraKm)) < SmallestSaving;
                return new RankedCandidate(c, cost, saves, overDetourLimit, savingUncertain);
            })
            .OrderBy(r => r.Cost)
            .ThenBy(r => r.Candidate.DistanceKm)
            .ThenBy(r => r.Candidate.PriceCents)
            .ThenBy(r => r.Id)
            .ToList();

        var reference = ranked.First(r => r.Id == closest.Id);

        // The reference is always pickable, so there's a best; a saving too small to be worth the drive leaves the reference.
        var best = ranked.First(r => r.Pickable);
        var pick = best.Saves >= SmallestSaving ? best : reference;

        // A stable sort of the ranked list: ties keep its order, by cost then distance.
        var cheapest = ranked.OrderBy(r => r.Candidate.PriceCents).First();

        var listed = new HashSet<long> { pick.Id, reference.Id, cheapest.Id };
        foreach (var pickable in (bool[])[true, false])
        {
            var others = ranked
                .Where(r => r.Pickable == pickable && r.Saves >= SmallestSaving && !listed.Contains(r.Id))
                .Take(ListedPerGroup)
                .Select(r => r.Id)
                .ToList();
            listed.UnionWith(others);
        }

        return new NearbyRanking(ranked, reference, pick, cheapest, ranked.Where(r => listed.Contains(r.Id)).ToList());
    }

    /// <summary>The extra km of an estimated distance with roads <see cref="CautiousRoadFactor"/> times the straight line.</summary>
    private static double Cautious(double extraKm) => extraKm * CautiousRoadFactor / RoadFactor;
}
