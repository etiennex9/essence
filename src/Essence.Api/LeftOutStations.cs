using Essence.Contracts;

namespace Essence.Api;

/// <summary>A station a ranking left out (<see cref="Essence.Ranking.Arrivals.LeaveOutClosed"/>), with what it would have cost.</summary>
/// <param name="CostDollars">The fill-up with the driving it takes (<see cref="Essence.Ranking.Costs.Fill"/>).</param>
/// <param name="EtaMinutes">How long getting there would take, one way.</param>
public sealed record LeftOutCandidate(SnapshotStation Station, decimal PriceCents, decimal CostDollars, double EtaMinutes);

/// <summary>
/// The stations Best near you and Best on the way say they left out (<see cref="BestNearby.LeftOut"/>,
/// <see cref="TripPlan.LeftOut"/>): only those cheaper per litre than the pick, the others not being missed, and at most
/// <see cref="Max"/>, lowest fill-up cost first.
/// </summary>
public static class LeftOutStations
{
    public const int Max = 2;

    /// <param name="pickPriceCents">The pick's price; null when there's no pick, and nothing is said.</param>
    /// <returns>Null when none is cheaper, so an answer with nothing left out is as an older API's.</returns>
    public static IReadOnlyList<LeftOutStation>? Of(
        IEnumerable<LeftOutCandidate> leftOut, IReadOnlyDictionary<long, SnapshotHours> hours, decimal? pickPriceCents)
    {
        if (pickPriceCents is not { } limit)
        {
            return null;
        }

        var said = leftOut
            .Where(c => c.PriceCents < limit && hours.ContainsKey(c.Station.Id))
            .OrderBy(c => c.CostDollars)
            .ThenBy(c => c.PriceCents)
            .ThenBy(c => c.Station.Id)
            .Take(Max)
            .Select(c => new LeftOutStation(
                c.Station.Id, c.Station.Name, c.Station.Brand, c.Station.Address, c.Station.Latitude, c.Station.Longitude,
                c.PriceCents, hours[c.Station.Id].Dto, Math.Round(c.EtaMinutes, 1)))
            .ToList();
        return said.Count > 0 ? said : null;
    }
}
