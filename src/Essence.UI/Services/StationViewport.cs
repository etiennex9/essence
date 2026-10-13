using Essence.Contracts;

namespace Essence.UI.Services;

public enum PriceTone
{
    None,
    Low,
    Mid,
    High,
}

/// <summary>A station to draw, with the price of the selected fuel (null when the station doesn't list it).</summary>
/// <param name="ZIndex">Higher draws on top: the cheapest priced station has the highest value, stations without a price 0.</param>
public sealed record VisibleStation(NearbyStation Station, CurrentPriceDto? Price, PriceTone Tone, int ZIndex)
{
    public bool HasPrice => StationViewport.IsPriced(Price);
}

/// <summary>
/// The stations loaded around the map, for one fuel. Each map move fetches the stations nearest the center and merges them in;
/// <see cref="Visible"/> picks the ones to draw and ranks their prices against each other.
/// </summary>
public sealed class StationViewport(int maxCached = StationViewport.DefaultMaxCached, int maxMarkers = StationViewport.DefaultMaxMarkers)
{
    /// <summary>Below this zoom the view is wider than the API's 50 km radius allows, so nothing is fetched.</summary>
    public const double MinFetchZoom = 9;
    public const double MaxRadiusKm = 50;
    public const int FetchLimit = 100;
    public const int DefaultMaxCached = 1000;
    public const int DefaultMaxMarkers = 150;

    /// <summary>Stations just outside the edges are kept so markers don't pop in and out while panning.</summary>
    private const double BoundsPadding = 0.1;

    private readonly Dictionary<long, NearbyStation> _stations = [];

    public string? Fuel { get; private set; }

    public int Count => _stations.Count;

    /// <summary>Switches the fuel, clearing the stations loaded for the previous one. Returns false if it's unchanged.</summary>
    public bool SetFuel(string fuel)
    {
        if (string.Equals(fuel, Fuel, StringComparison.Ordinal))
        {
            return false;
        }

        Fuel = fuel;
        _stations.Clear();
        return true;
    }

    public static bool ShouldFetch(Viewport viewport) => viewport.Zoom >= MinFetchZoom;

    /// <summary>Distance from the center to the farthest corner, rounded up to 100 m and capped at the API's maximum.</summary>
    public static double FetchRadiusKm(Viewport viewport)
    {
        var corner = Math.Max(
            Geo.DistanceKm(viewport.Center, new GeoPoint(viewport.North, viewport.East)),
            Geo.DistanceKm(viewport.Center, new GeoPoint(viewport.South, viewport.West)));
        return Math.Clamp(Math.Ceiling(corner * 10) / 10, 0.1, MaxRadiusKm);
    }

    /// <summary>Adds or refreshes stations. Past the cache size, the ones farthest from <paramref name="center"/> are dropped.</summary>
    public void Merge(IEnumerable<NearbyStation> stations, GeoPoint center)
    {
        foreach (var station in stations)
        {
            _stations[station.Id] = station;
        }

        var excess = _stations.Count - maxCached;
        if (excess <= 0)
        {
            return;
        }

        var farthest = _stations.Values
            .OrderByDescending(s => Geo.DistanceKm(center, new GeoPoint(s.Latitude, s.Longitude)))
            .Take(excess)
            .Select(s => s.Id)
            .ToList();
        foreach (var id in farthest)
        {
            _stations.Remove(id);
        }
    }

    public NearbyStation? Find(long id) => _stations.GetValueOrDefault(id);

    /// <summary>
    /// The stations in view (closest to the center first, at most <c>maxMarkers</c>), each with its tone among the priced
    /// ones in view and its drawing order.
    /// </summary>
    public IReadOnlyList<VisibleStation> Visible(Viewport viewport)
    {
        var inView = _stations.Values
            .Where(s => viewport.Contains(s.Latitude, s.Longitude, BoundsPadding))
            .OrderBy(s => Geo.DistanceKm(viewport.Center, new GeoPoint(s.Latitude, s.Longitude)))
            .Take(maxMarkers)
            .Select(s => (Station: s, Price: PriceOf(s)))
            .ToList();

        var prices = inView.Where(x => IsPriced(x.Price)).Select(x => x.Price!.PriceCents!.Value).Order().ToList();

        // The most expensive gets 1, the cheapest prices.Count; ties are broken by id so the order is stable.
        var zIndex = inView
            .Where(x => IsPriced(x.Price))
            .OrderByDescending(x => x.Price!.PriceCents)
            .ThenByDescending(x => x.Station.Id)
            .Select((x, rank) => (x.Station.Id, Z: rank + 1))
            .ToDictionary(x => x.Id, x => x.Z);

        return inView
            .Select(x => IsPriced(x.Price)
                ? new VisibleStation(x.Station, x.Price, ToneOf(x.Price!.PriceCents!.Value, prices), zIndex[x.Station.Id])
                : new VisibleStation(x.Station, x.Price, PriceTone.None, 0))
            .ToList();
    }

    /// <summary>
    /// Tertile of <paramref name="price"/> by the share of strictly cheaper prices, so equal prices share a tone.
    /// No tone with fewer than 3 prices or when they're all the same.
    /// </summary>
    public static PriceTone ToneOf(decimal price, IReadOnlyList<decimal> sortedPrices)
    {
        var count = sortedPrices.Count;
        if (count < 3 || sortedPrices[0] == sortedPrices[^1])
        {
            return PriceTone.None;
        }

        var cheaper = 0;
        while (cheaper < count && sortedPrices[cheaper] < price)
        {
            cheaper++;
        }

        return cheaper * 3 < count ? PriceTone.Low
            : cheaper * 3 < count * 2 ? PriceTone.Mid
            : PriceTone.High;
    }

    internal static bool IsPriced(CurrentPriceDto? price) => price is { IsAvailable: true, PriceCents: not null };

    private CurrentPriceDto? PriceOf(NearbyStation station) =>
        station.Prices.FirstOrDefault(p => string.Equals(p.Fuel, Fuel, StringComparison.OrdinalIgnoreCase));
}
