namespace Essence.Ranking;

/// <summary>A station as <see cref="CheapStations"/> reads it: where it is, and its prices for the fuel.</summary>
/// <param name="MembersOnly">A Costco: rated like the others, but no one's neighbour, since most drivers can't buy there.</param>
public sealed record CheapCandidate(long Id, double Latitude, double Longitude, bool MembersOnly, StationPrices Prices);

/// <summary>The stations usually among the cheapest around them.</summary>
/// <param name="AsOf">The latest import the history was read to.</param>
/// <param name="Days">How many days of history were compared: <see cref="TrendRules.CheapWindow"/>'s, fewer on a younger history.</param>
/// <param name="Rated">How many stations had enough moments counted to be rated, for the log.</param>
public sealed record UsualCheapness(DateTimeOffset AsOf, int Days, int Rated, StationSet Stations);

/// <summary>
/// Which stations are usually cheap: every <see cref="TrendRules.CheapStep"/> of the last <see cref="TrendRules.CheapWindow"/>
/// that the data covers, each station's price is ranked among its neighbours' (the stations within
/// <see cref="TrendRules.CheapWithinKm"/>, Costco aside), and it's usually cheap when it was in the cheapest fifth on
/// <see cref="TrendRules.CheapShare"/> of those moments. A moment with fewer than <see cref="TrendRules.CheapMinOthers"/>
/// neighbours priced doesn't count, nor one when the station had no price.
/// </summary>
public static class CheapStations
{
    /// <summary>Degrees of latitude a little wider than <see cref="TrendRules.CheapWithinKm"/>: only stations this far apart north–south are measured.</summary>
    private const double LatitudeBand = TrendRules.CheapWithinKm / 111.0;

    /// <summary>As of the latest import at or before <paramref name="now"/>; null before the first.</summary>
    public static UsualCheapness? Find(IReadOnlyList<CheapCandidate> stations, Coverage coverage, DateTimeOffset now)
    {
        if (coverage.LatestAtOrBefore(now.UtcTicks) is not { } asOf)
        {
            return null;
        }

        var neighbours = NeighboursOf(stations);
        var count = stations.Count;
        var prices = new int[count];
        var counted = new int[count];
        var cheap = new int[count];
        var step = TrendRules.CheapStep.Ticks;
        var from = asOf - TrendRules.CheapWindow.Ticks;
        from = from - from % step + step;
        long? first = null;
        for (var at = from; at <= asOf; at += step)
        {
            if (Trends.ReadPoint(coverage, at, asOf) is not { } read)
            {
                continue;
            }

            first ??= at;
            for (var i = 0; i < count; i++)
            {
                prices[i] = stations[i].Prices.TenthsAt(read, asOf) ?? -1;
            }

            for (var i = 0; i < count; i++)
            {
                var price = prices[i];
                if (price < 0)
                {
                    continue;
                }

                // Twice the neighbours cheaper, plus those at the same price: each of those counts half.
                int priced = 0, below = 0;
                foreach (var j in neighbours[i])
                {
                    var other = prices[j];
                    if (other < 0)
                    {
                        continue;
                    }

                    priced++;
                    below += other < price ? 2 : other == price ? 1 : 0;
                }

                if (priced < TrendRules.CheapMinOthers)
                {
                    continue;
                }

                counted[i]++;
                if (below / (2.0 * priced) < TrendRules.CheapRank)
                {
                    cheap[i]++;
                }
            }
        }

        var rated = 0;
        var usual = new List<long>();
        for (var i = 0; i < count; i++)
        {
            if (counted[i] < TrendRules.CheapMinSamples)
            {
                continue;
            }

            rated++;
            if (cheap[i] / (double)counted[i] >= TrendRules.CheapShare)
            {
                usual.Add(stations[i].Id);
            }
        }

        var days = first is { } start ? (int)Math.Round((asOf - start) / (double)TimeSpan.TicksPerDay) : 0;
        return new UsualCheapness(new DateTimeOffset(asOf, TimeSpan.Zero), days, rated, StationSet.Of(usual));
    }

    /// <summary>Each station's neighbours, by index: those within <see cref="TrendRules.CheapWithinKm"/>, Costco aside.</summary>
    private static int[][] NeighboursOf(IReadOnlyList<CheapCandidate> stations)
    {
        var lists = new List<int>[stations.Count];
        for (var i = 0; i < lists.Length; i++)
        {
            lists[i] = [];
        }

        var order = Enumerable.Range(0, stations.Count).OrderBy(i => stations[i].Latitude).ToArray();
        for (var a = 0; a < order.Length; a++)
        {
            var one = stations[order[a]];
            for (var b = a + 1; b < order.Length && stations[order[b]].Latitude - one.Latitude <= LatitudeBand; b++)
            {
                var other = stations[order[b]];
                if (Geo.DistanceKm(one.Latitude, one.Longitude, other.Latitude, other.Longitude) > TrendRules.CheapWithinKm)
                {
                    continue;
                }

                if (!other.MembersOnly)
                {
                    lists[order[a]].Add(order[b]);
                }

                if (!one.MembersOnly)
                {
                    lists[order[b]].Add(order[a]);
                }
            }
        }

        return [.. lists.Select(l => l.ToArray())];
    }
}
