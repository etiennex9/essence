using Essence.UI.Services;

namespace Essence.UI.Tests;

public class PillPlacementTests
{
    private const double Latitude = 45.5;
    private const double Longitude = -73.57;

    /// <summary>High enough that no test hits the cap, unless it means to.</summary>
    private const double Uncapped = 22;

    [Fact]
    public void A_lone_station_always_shows_its_price()
    {
        var place = PillPlacement.Place([At(1)])[1];

        Assert.Equal(new PillPlace(1, 0), place);
    }

    [Fact]
    public void The_cheapest_draws_on_top_and_stations_without_a_price_under_all()
    {
        var places = PillPlacement.Place([At(1, price: 167.3m), At(2, price: 159.9m), At(3, price: null), At(4, price: 164.9m)]);

        Assert.True(places[2].Z > places[4].Z);
        Assert.True(places[4].Z > places[1].Z);
        Assert.True(places[1].Z > places[3].Z);
    }

    [Fact]
    public void Equal_prices_draw_the_smaller_id_on_top()
    {
        var places = PillPlacement.Place([At(7), At(3)]);

        Assert.True(places[3].Z > places[7].Z);
    }

    [Fact]
    public void Of_two_pills_side_by_side_the_cheaper_shows_its_price_first()
    {
        var places = PillPlacement.Place([At(1, price: 165m), At(2, east: 200, price: 160m)], allPillsFromZoom: Uncapped);

        Assert.Equal(0, places[2].PillFrom);
        // 62 px apart (two 68 px pills, 6 px into each other): 200 m is 14.9 px at zoom 12, here.
        Assert.InRange(places[1].PillFrom, 14.0, 14.1);
    }

    [Fact]
    public void Pills_may_reach_a_few_pixels_into_each_other()
    {
        var places = PillPlacement.Place([At(1, price: 165m), At(2, east: 200, price: 160m)], allPillsFromZoom: Uncapped);

        var apart = PixelsEastWest(Longitude, East(200), places[1].PillFrom);
        Assert.InRange(apart, 61.5, 62.5);
    }

    [Fact]
    public void From_zoom_12_every_station_shows_its_price()
    {
        var places = PillPlacement.Place([At(1, price: 165m), At(2, east: 200, price: 160m)]);

        Assert.Equal(PillPlacement.AllPillsFromZoom, places[1].PillFrom);
    }

    [Fact]
    public void Pills_one_above_the_other_clear_sooner_than_side_by_side()
    {
        var places = PillPlacement.Place([At(1, price: 165m), At(2, north: 100, price: 160m)], allPillsFromZoom: Uncapped);

        // 27 px between their points: the 33 px of the lower one's pill and tail, 6 px into the upper one.
        Assert.InRange(places[1].PillFrom, 13.8, 13.9);
    }

    [Fact]
    public void Stations_at_the_same_spot_take_turns_from_zoom_12()
    {
        var places = PillPlacement.Place([At(1, price: 165m), At(2, price: 160m)]);

        Assert.Equal(0, places[2].PillFrom);
        Assert.Equal(PillPlacement.AllPillsFromZoom, places[1].PillFrom);
    }

    [Fact]
    public void A_station_squeezed_between_two_cheaper_ones_waits_for_both()
    {
        // 1 is the cheapest, 2 between 1 and 3: 3, farther from both, shows its price before 2 does.
        var places = PillPlacement.Place(
            [At(1, price: 150m), At(2, east: 200, price: 155m), At(3, east: 450, price: 160m)], allPillsFromZoom: Uncapped);

        Assert.True(places[3].PillFrom < places[2].PillFrom);
        Assert.InRange(places[2].PillFrom, 14.0, 14.1);
    }

    [Fact]
    public void A_pricier_pill_doesnt_open_over_a_cheaper_stations_dot()
    {
        // 2 is a dot until zoom 17 (1 is 20 m away); 3, 100 m south of 2, would clear 2's pill and 1's at 13.8, but 2's dot
        // hangs 7.5 px below its point, into 3's pill.
        var places = PillPlacement.Place(
            [At(1, east: 20, price: 150m), At(2, price: 155m), At(3, north: -100, price: 160m)], allPillsFromZoom: Uncapped);

        Assert.True(places[2].PillFrom > 17);
        Assert.InRange(places[3].PillFrom, 14.1, 14.25);
    }

    [Fact]
    public void A_cheaper_price_may_cover_a_pricier_stations_dot()
    {
        var places = PillPlacement.Place([At(1, price: 150m), At(2, north: 100, price: 160m)], allPillsFromZoom: Uncapped);

        Assert.Equal(0, places[1].PillFrom);
        Assert.True(places[2].PillFrom > 13);
    }

    [Fact]
    public void The_selected_station_and_the_pick_always_show_their_price_and_the_others_make_room()
    {
        var plain = PillPlacement.Place([At(1, price: 165m), At(2, east: 200, price: 160m)], allPillsFromZoom: Uncapped);
        var places = PillPlacement.Place(
            [At(1, price: 165m) with { Selected = true }, At(2, east: 200, price: 160m), At(3, east: -100, price: 170m) with { Pick = true }],
            allPillsFromZoom: Uncapped);

        Assert.Equal(0, places[1].PillFrom);
        Assert.Equal(0, places[3].PillFrom);
        Assert.True(places[1].Z > places[3].Z);
        Assert.True(places[3].Z > places[2].Z);
        // The selected pill is larger.
        Assert.True(places[2].PillFrom > plain[1].PillFrom + 0.1);
    }

    [Fact]
    public void Faded_stations_make_room_for_the_ones_listed()
    {
        var places = PillPlacement.Place([At(1, price: 150m) with { Dimmed = true }, At(2, east: 200, price: 160m)]);

        Assert.Equal(0, places[2].PillFrom);
        Assert.True(places[1].PillFrom > 0);
        Assert.True(places[2].Z > places[1].Z);
    }

    [Fact]
    public void Closed_stations_make_room_for_the_open_ones()
    {
        var places = PillPlacement.Place([At(1, price: 150m) with { Closed = true }, At(2, east: 200, price: 160m)]);

        Assert.Equal(0, places[2].PillFrom);
        Assert.True(places[1].PillFrom > 0);
        Assert.True(places[2].Z > places[1].Z);
    }

    [Fact]
    public void Closed_stations_come_after_the_faded_open_ones_and_before_those_without_a_price()
    {
        var places = PillPlacement.Place(
        [
            At(1, price: 150m) with { Closed = true, Dimmed = true },
            At(2, price: 151m) with { Closed = true },
            At(3, price: 152m) with { Dimmed = true },
            At(4, price: null),
            At(5, price: 153m),
        ]);

        Assert.Equal([5L, 3L, 2L, 1L, 4L], places.OrderByDescending(p => p.Value.Z).Select(p => p.Key));
    }

    [Fact]
    public void Stations_without_a_price_make_room_for_the_others()
    {
        var places = PillPlacement.Place([At(1, price: null), At(2, east: 200, price: 170m) with { Dimmed = true }]);

        Assert.Equal(0, places[2].PillFrom);
        Assert.True(places[1].PillFrom > 0);
    }

    [Fact]
    public void A_long_flag_pushes_its_neighbours_away()
    {
        var plain = PillPlacement.Place([At(1, price: 165m) with { Second = true }, At(2, east: 200, price: 160m)], allPillsFromZoom: Uncapped);
        var flagged = PillPlacement.Place(
            [At(1, price: 165m) with { Second = true, Flag = "Plus petit détour" }, At(2, east: 200, price: 160m)], allPillsFromZoom: Uncapped);

        Assert.True(flagged[2].PillFrom > plain[2].PillFrom + 0.5);
    }

    [Fact]
    public void A_favorites_star_reaches_out_on_the_right_only()
    {
        PillPlace Neighbour(double east, bool favorite) => PillPlacement.Place(
            [At(1, price: 150m) with { Favorite = favorite }, At(2, east: east, price: 160m)], allPillsFromZoom: Uncapped)[2];

        Assert.True(Neighbour(200, favorite: true).PillFrom > Neighbour(200, favorite: false).PillFrom);
        Assert.Equal(Neighbour(-200, favorite: false).PillFrom, Neighbour(-200, favorite: true).PillFrom, 6);
    }

    [Fact]
    public void A_price_waits_until_it_clears_the_trips_labels()
    {
        var label = PillObstacle.Label(new GeoPoint(Latitude, Longitude), "Maison");
        var places = PillPlacement.Place([At(1, north: 100)], [label], allPillsFromZoom: Uncapped);

        // 29 px: the label's 35, 6 into it.
        Assert.InRange(places[1].PillFrom, 13.9, 14.0);
    }

    [Fact]
    public void Every_zoom_is_a_number_from_0_to_12()
    {
        List<PillCandidate> crowd = [];
        for (var i = 0; i < 300; i++)
        {
            // A dense grid, some stations sharing a spot, a few without a price.
            crowd.Add(At(i, north: i / 20 * 60, east: i % 20 * 40 / (i % 7 == 0 ? 2 : 1), price: i % 11 == 0 ? null : 150m + i % 13));
        }

        var places = PillPlacement.Place(crowd, [PillObstacle.Pin(new GeoPoint(Latitude, Longitude))]);

        Assert.All(places.Values, p => Assert.InRange(p.PillFrom, 0, PillPlacement.AllPillsFromZoom));
        Assert.Equal(Enumerable.Range(1, 300), places.Values.Select(p => p.Z).Order());
    }

    private static PillCandidate At(long id, double north = 0, double east = 0, decimal? price = 160m) =>
        new(id, Latitude + north / 111_132, East(east), price);

    private static double East(double meters) => Longitude + meters / (111_320 * Math.Cos(Latitude * Math.PI / 180));

    /// <summary>Screen pixels between two longitudes at a zoom: the world is 512 × 2^zoom pixels around.</summary>
    private static double PixelsEastWest(double west, double east, double zoom) => (east - west) / 360 * 512 * Math.Pow(2, zoom);
}
