using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class StationViewportTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>About 4.5 km × 4.5 km around downtown Montréal.</summary>
    private static readonly Viewport Montreal = new(45.5017, -73.5673, -73.596, 45.482, -73.539, 45.522, 13);

    [Fact]
    public void Fetch_radius_reaches_the_corners()
    {
        var radius = StationViewport.FetchRadiusKm(Montreal);
        var corner = Geo.DistanceKm(Montreal.Center, new GeoPoint(Montreal.North, Montreal.East));
        Assert.InRange(radius, corner, corner + 0.1);
    }

    [Fact]
    public void Fetch_radius_is_capped_at_the_api_maximum()
    {
        var province = new Viewport(47, -72, -80, 44, -64, 50, 6);
        Assert.Equal(StationViewport.MaxRadiusKm, StationViewport.FetchRadiusKm(province));
    }

    [Theory]
    [InlineData(8.9, false)]
    [InlineData(9, true)]
    [InlineData(15, true)]
    public void Fetches_only_from_zoom_9(double zoom, bool expected) =>
        Assert.Equal(expected, StationViewport.ShouldFetch(Montreal with { Zoom = zoom }));

    [Fact]
    public void Changing_the_fuel_clears_the_stations()
    {
        var viewport = new StationViewport();
        viewport.SetFuel("Régulier");
        viewport.Merge([Station(1, 45.50, -73.57, 160m)], Montreal.Center);

        Assert.False(viewport.SetFuel("Régulier"));
        Assert.Equal(1, viewport.Count);
        Assert.True(viewport.SetFuel("Diesel"));
        Assert.Equal(0, viewport.Count);
    }

    [Fact]
    public void Merge_refreshes_existing_stations()
    {
        var viewport = Loaded(Station(1, 45.50, -73.57, 160m));
        viewport.Merge([Station(1, 45.50, -73.57, 155m)], Montreal.Center);

        Assert.Equal(1, viewport.Count);
        Assert.Equal(155m, viewport.Find(1)!.Prices[0].PriceCents);
    }

    [Fact]
    public void Merge_evicts_the_farthest_stations_past_the_cache_size()
    {
        var viewport = new StationViewport(maxCached: 2);
        viewport.SetFuel("Régulier");
        viewport.Merge([Station(1, 45.50, -73.57, 160m), Station(2, 46.81, -71.21, 160m), Station(3, 45.51, -73.56, 160m)], Montreal.Center);

        Assert.Equal(2, viewport.Count);
        Assert.Null(viewport.Find(2)); // Québec City, the farthest from Montréal
    }

    [Fact]
    public void Visible_keeps_stations_in_the_padded_bounds_only()
    {
        var viewport = Loaded(
            Station(1, 45.50, -73.57, 160m),
            Station(2, 45.523, -73.57, 160m), // just north of the bounds, within the 10% padding
            Station(3, 46.81, -71.21, 160m)); // Québec City

        var ids = viewport.Visible(Montreal).Select(v => v.Station.Id).Order();
        Assert.Equal([1L, 2L], ids);
    }

    [Fact]
    public void Visible_keeps_the_closest_stations_up_to_the_marker_limit()
    {
        var viewport = new StationViewport(maxMarkers: 2);
        viewport.SetFuel("Régulier");
        viewport.Merge([Station(1, 45.5017, -73.5673, 160m), Station(2, 45.515, -73.58, 160m), Station(3, 45.503, -73.568, 160m)], Montreal.Center);

        Assert.Equal([1L, 3L], viewport.Visible(Montreal).Select(v => v.Station.Id));
    }

    [Fact]
    public void Tones_split_the_visible_prices_in_thirds_and_cheapest_draws_on_top()
    {
        var viewport = Loaded(
            Station(1, 45.50, -73.57, 159.9m),
            Station(2, 45.50, -73.56, 164.9m),
            Station(3, 45.51, -73.57, 167.3m),
            Station(4, 45.51, -73.56, null));

        var visible = viewport.Visible(Montreal).ToDictionary(v => v.Station.Id);

        Assert.Equal(PriceTone.Low, visible[1].Tone);
        Assert.Equal(PriceTone.Mid, visible[2].Tone);
        Assert.Equal(PriceTone.High, visible[3].Tone);
        Assert.Equal(PriceTone.None, visible[4].Tone);
        Assert.False(visible[4].HasPrice);
        Assert.True(visible[1].ZIndex > visible[2].ZIndex);
        Assert.True(visible[2].ZIndex > visible[3].ZIndex);
        Assert.Equal(0, visible[4].ZIndex);
    }

    [Theory]
    [InlineData(new[] { 150.0, 150.0, 160.0 }, 150.0, PriceTone.Low)]
    [InlineData(new[] { 150.0, 150.0, 160.0 }, 160.0, PriceTone.High)]
    [InlineData(new[] { 150.0, 160.0 }, 150.0, PriceTone.None)]
    [InlineData(new[] { 155.0, 155.0, 155.0 }, 155.0, PriceTone.None)]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }, 2.0, PriceTone.Low)]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }, 3.0, PriceTone.Mid)]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }, 5.0, PriceTone.High)]
    public void ToneOf_uses_the_share_of_cheaper_prices(double[] prices, double price, PriceTone expected) =>
        Assert.Equal(expected, StationViewport.ToneOf((decimal)price, prices.Select(p => (decimal)p).ToList()));

    [Fact]
    public void A_station_without_the_selected_fuel_has_no_price()
    {
        var viewport = new StationViewport();
        viewport.SetFuel("Diesel");
        viewport.Merge([Station(1, 45.50, -73.57, 160m)], Montreal.Center);

        var visible = Assert.Single(viewport.Visible(Montreal));
        Assert.Null(visible.Price);
    }

    private static StationViewport Loaded(params NearbyStation[] stations)
    {
        var viewport = new StationViewport();
        viewport.SetFuel("Régulier");
        viewport.Merge(stations, Montreal.Center);
        return viewport;
    }

    private static NearbyStation Station(long id, double latitude, double longitude, decimal? regular) =>
        new(id, $"Station {id}", "Esso", $"{id} rue Principale", "H2X1Y4", "Montréal", latitude, longitude, 1,
            [new CurrentPriceDto("Régulier", regular, regular is not null, Since)]);
}
