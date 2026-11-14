using Essence.Data.Entities;
using Essence.Ingest.Hours;
using Essence.Ingest.Positions;
using Essence.Ranking;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Ingest.Tests;

/// <summary>
/// Which stations the ingest lists for review (<see cref="PositionFinder"/>): two kinds of position that agree far from
/// the feed's point, never one alone, and nothing of the station's at the feed's point. Points from the cases of
/// 2026-11-13: Newman's as they are, Papineauville's adapted so that the check finds it, which it doesn't on the real
/// data (its correction was added by hand).
/// </summary>
public sealed class PositionFinderTests
{
    /// <summary>About 1 m of latitude, in degrees.</summary>
    private const double Metre = 1 / 111_195.0;

    // Papineauville's Ultramar, 1952 côte des Cascades: the feed puts it about 360 m north-west of where it is. Adapted: on
    // OpenStreetMap the element has no address (here it has the station's), and Ultramar's store 56182 is 140 m south-west
    // of it (here it's next to it).
    private static readonly PositionStation Papineauville = new(
        10, "1952 cote des cascades, papineauville|J0V1R0", "ultramar", 45.663818, -75.024071, "1952", "J0V1R0",
        "1952 côte des Cascades, PAPINEAUVILLE");

    private static readonly SourceRecord PapineauvilleElement = Osm("node/12114619118", "ultramar", 45.6617918, -75.0204765, "1952", "J0V1R0");
    private static readonly SourceRecord PapineauvilleStore = Record("56182", "ultramar", 45.6618, -75.0206, "1952", "J0V1R0");

    // Couche-Tard #1268, 7067 boul. Newman, Montréal: Esso's gas, Circle K's store 6001268 400 m from the feed's point.
    private static readonly PositionStation Newman = new(
        20, "7067 boul. newman, montreal|H8N1X1", "esso", 45.447465, -73.615631, "7067", "H8N1X1", "7067 boul. Newman, Montréal")
    {
        StoreNumber = 1268,
    };

    private static readonly SourceRecord NewmanStore = Record("6001268", "couche-tard", 45.44763, -73.610487, "7067", "H8N1X1") with { StoreNumber = 1268 };

    /// <summary>Photon's address node for 7067 boulevard Newman: OpenStreetMap gives it another postal code.</summary>
    private static readonly PhotonPlace NewmanAddress = Place("node/11973108312", "place", "house", "7067", "H8N 0E1", "Montréal", 45.4476807, -73.6105273);

    private static SourceRecord Osm(string reference, string? brand, double latitude, double longitude, string? houseNumber = null, string? postcode = null) =>
        new(reference, brand, latitude, longitude, houseNumber, postcode, null, $"https://www.openstreetmap.org/{reference}", null, false);

    private static SourceRecord Record(string reference, string brand, double latitude, double longitude, string? houseNumber, string? postcode) =>
        new(reference, brand, latitude, longitude, houseNumber, postcode, "24/7", "https://example.com/locator", null, false);

    private static PhotonPlace Place(
        string reference, string key, string value, string? houseNumber, string? postcode, string? city, double latitude, double longitude) =>
        new(new LatLon(latitude, longitude), "house", key, value, null, houseNumber, "Boulevard Newman", postcode, null, null, city, null, "Québec", "CA", reference);

    private static PositionFinder Finder(IReadOnlyList<PositionStation> stations, SourceRecord[] osm, SourceRecord[]? locator = null, string source = "ultramar") =>
        new(stations, [new PositionSource("osm", true, osm), new PositionSource(source, false, locator ?? [])]);

    [Fact]
    public void An_element_and_a_store_that_agree_far_from_the_feed_s_point_make_a_candidate_at_their_mean()
    {
        var finder = Finder([Papineauville], [PapineauvilleElement], [PapineauvilleStore]);

        var positions = finder.Known(Papineauville);
        var found = finder.Candidate(Papineauville, positions);

        PositionEvidence[] expected =
        [
            new(PositionCandidate.FromOsm, "node/12114619118", 45.6617918, -75.0204765, 359),
            new(PositionCandidate.FromLocator, "ultramar/56182", 45.6618, -75.0206, 351),
        ];
        Assert.Equal(expected, positions);
        Assert.NotNull(found);
        Assert.Equal((45.661796, -75.020538, 355), (found.Latitude, found.Longitude, found.DistanceM));
        Assert.Equal(positions, found.Evidence);
        Assert.Same(Papineauville, found.Station);
    }

    [Fact]
    public void Without_the_address_search_OpenStreetMap_and_a_locator_are_enough()
    {
        // NeedsGeocode says to ask, but with no address search nothing is asked, and the two agree all the same.
        var finder = Finder([Papineauville], [PapineauvilleElement], [PapineauvilleStore]);

        var positions = finder.Known(Papineauville);

        Assert.True(finder.NeedsGeocode(Papineauville, positions));
        Assert.NotNull(finder.Candidate(Papineauville, positions));
    }

    [Fact]
    public void One_kind_alone_is_never_enough()
    {
        var element = Finder([Papineauville], [PapineauvilleElement]);
        var store = Finder([Papineauville], [], [PapineauvilleStore]);

        Assert.Null(element.Candidate(Papineauville, element.Known(Papineauville)));
        Assert.Null(store.Candidate(Papineauville, store.Known(Papineauville)));
    }

    [Fact]
    public void Two_positions_of_the_same_kind_are_one_kind()
    {
        var twin = PapineauvilleElement with { Ref = "way/999", Latitude = 45.6618 };
        var finder = Finder([Papineauville], [PapineauvilleElement, twin]);

        var positions = finder.Known(Papineauville);

        Assert.Equal(2, positions.Count);
        Assert.Null(finder.Candidate(Papineauville, positions));
    }

    [Fact]
    public void An_element_of_its_brand_at_the_feed_s_point_keeps_it_off_the_list()
    {
        // Without an address: it says nothing of where the station is, but that the feed's point may be right.
        var atTheFeedPoint = Osm("node/1", "ultramar", Papineauville.Latitude + 30 * Metre, Papineauville.Longitude);
        var finder = Finder([Papineauville], [PapineauvilleElement, atTheFeedPoint], [PapineauvilleStore]);

        var positions = finder.Known(Papineauville);

        Assert.True(finder.AtTheFeedPoint(Papineauville, positions));
        Assert.False(finder.NeedsGeocode(Papineauville, positions));
        Assert.Null(finder.Candidate(Papineauville, positions));
    }

    [Fact]
    public void A_store_of_its_brand_or_a_position_of_its_own_at_the_feed_s_point_keeps_it_off_the_list_too()
    {
        var store = Record("1", "ultramar", Papineauville.Latitude - 70 * Metre, Papineauville.Longitude, null, null);
        var ownAddress = PapineauvilleStore with { Ref = "2", Latitude = Papineauville.Latitude + 20 * Metre, Longitude = Papineauville.Longitude };
        var otherBrand = Osm("node/2", "esso", Papineauville.Latitude + 10 * Metre, Papineauville.Longitude);

        var byStore = Finder([Papineauville], [PapineauvilleElement], [PapineauvilleStore, store]);
        var byOwn = Finder([Papineauville], [PapineauvilleElement], [PapineauvilleStore, ownAddress]);
        var byOtherBrand = Finder([Papineauville], [PapineauvilleElement, otherBrand], [PapineauvilleStore]);

        Assert.Null(byStore.Candidate(Papineauville, byStore.Known(Papineauville)));
        Assert.Null(byOwn.Candidate(Papineauville, byOwn.Known(Papineauville)));
        Assert.NotNull(byOtherBrand.Candidate(Papineauville, byOtherBrand.Known(Papineauville)));
    }

    [Fact]
    public void They_must_agree_within_75_m_and_both_be_more_than_150_m_away()
    {
        var apart = PapineauvilleStore with { Latitude = PapineauvilleElement.Latitude + 80 * Metre, Longitude = PapineauvilleElement.Longitude };
        var near = Papineauville with { Latitude = 45.6617918 + 140 * Metre, Longitude = -75.0204765 };
        var nearStore = PapineauvilleStore with { Latitude = 45.6617918 - 40 * Metre, Longitude = -75.0204765 };

        var disagree = Finder([Papineauville], [PapineauvilleElement], [apart]);
        var tooClose = Finder([near], [PapineauvilleElement], [nearStore]);

        Assert.Null(disagree.Candidate(Papineauville, disagree.Known(Papineauville)));
        var positions = tooClose.Known(near);
        Assert.Equal(new[] { 140, 180 }, positions.Select(p => p.DistanceM));
        Assert.Null(tooClose.Candidate(near, positions));
    }

    [Fact]
    public void A_position_is_at_the_station_s_house_number_and_postal_code_within_30_km_of_its_brand_or_none_on_OpenStreetMap()
    {
        var noBrand = Osm("way/1", null, 45.6617918, -75.0204765, "1952", "J0V1R0");
        var otherBrand = Osm("way/2", "crevier", 45.6617918, -75.0204765, "1952", "J0V1R0");
        var otherNumber = Osm("way/3", "ultramar", 45.6617918, -75.0204765, "1950", "J0V1R0");
        var postcodeOnly = Osm("way/4", "ultramar", 45.6617918, -75.0204765, null, "J0V1R0");
        var farAway = Osm("way/5", "ultramar", 45.663818 - 31_000 * Metre, -75.024071, "1952", "J0V1R0");
        var storeOfNoBrand = PapineauvilleStore with { Ref = "9", BrandFamily = null };

        var finder = Finder([Papineauville], [noBrand, otherBrand, otherNumber, postcodeOnly, farAway], [storeOfNoBrand]);

        Assert.Equal(new[] { "way/1" }, finder.Known(Papineauville).Select(p => p.Ref));
    }

    [Fact]
    public void A_store_is_the_station_s_by_the_number_in_its_name_whatever_its_address_unless_two_stations_carry_it()
    {
        var store = NewmanStore with { HouseNumber = null, Postcode = null };
        var twin = Newman with { Id = 21, NaturalKey = "1 rue x, montreal|H1H1H1" };

        var finder = Finder([Newman], [], [store], "circlek");
        var twice = Finder([Newman, twin], [], [store], "circlek");

        Assert.Equal(
            new[] { new PositionEvidence(PositionCandidate.FromLocator, "circlek/6001268", 45.44763, -73.610487, 402) },
            finder.Known(Newman));
        Assert.Empty(twice.Known(Newman));
    }

    [Fact]
    public void A_store_and_the_address_search_s_point_for_the_address_make_a_candidate()
    {
        var finder = Finder([Newman], [], [NewmanStore], "circlek");
        var known = finder.Known(Newman);

        Assert.True(finder.NeedsGeocode(Newman, known));
        var address = finder.FromPhoton(Newman, [NewmanAddress]);
        var found = finder.Candidate(Newman, [.. known, .. address]);

        Assert.Equal(new[] { new PositionEvidence(PositionCandidate.FromPhoton, "node/11973108312", 45.4476807, -73.6105273, 399) }, address);
        Assert.NotNull(found);
        Assert.Equal(new[] { "locator circlek/6001268", "photon node/11973108312" }, found.Evidence.Select(e => $"{e.Kind} {e.Ref}"));
        Assert.Equal(400, found.DistanceM);
    }

    [Fact]
    public void Only_a_place_at_the_station_s_house_number_and_postal_code_or_town_counts_never_a_street_s_range_nor_a_fuel_element()
    {
        PhotonPlace[] places =
        [
            NewmanAddress with { OsmRef = "way/1", OsmKey = "place", OsmValue = "house_number" },
            NewmanAddress with { OsmRef = "node/2", OsmKey = "amenity", OsmValue = "fuel" },
            NewmanAddress with { OsmRef = "way/3", OsmKey = "shop", OsmValue = "convenience" },
            NewmanAddress with { OsmRef = "node/4", HouseNumber = "7069" },
            NewmanAddress with { OsmRef = "node/5", City = "Laval" },
            NewmanAddress with { OsmRef = "node/6", City = "Laval", Postcode = "H8N 1X1" },
            NewmanAddress with { OsmRef = "node/7", Type = "street" },
            NewmanAddress with { OsmRef = null },
            NewmanAddress with { OsmRef = "way/8", OsmKey = "building", OsmValue = "yes", HouseNumber = "7067A" },
        ];
        // way/3 is a fuel element's shop, Photon listing the element once per tag: OpenStreetMap's kind already.
        var finder = Finder([Newman], [Osm("way/3", "esso", 45.4476, -73.6105)], [NewmanStore], "circlek");

        Assert.Equal(new[] { "node/6", "way/8" }, finder.FromPhoton(Newman, places).Select(p => p.Ref));
    }

    [Fact]
    public void The_address_search_is_asked_only_for_a_station_with_a_position_far_off_and_nothing_at_the_feed_s_point()
    {
        var near = Newman with { Latitude = 45.44763 + 100 * Metre, Longitude = -73.610487 };
        var alone = Finder([Newman, near], [], [], "circlek");
        var finder = Finder([near], [], [NewmanStore], "circlek");

        Assert.False(alone.NeedsGeocode(Newman, alone.Known(Newman)));
        Assert.False(finder.NeedsGeocode(near, finder.Known(near)));
    }

    [Theory]
    [InlineData("1918 boul. Tadoussac, Saint-Fulgence", "1918 boulevard Tadoussac, Saint-Fulgence")]
    [InlineData("306 ch. Ridge, Saint-Bernard-de-Lacolle", "306 chemin Ridge, Saint-Bernard-de-Lacolle")]
    [InlineData("46  rte 138, Baie-Trinité", "46 route 138, Baie-Trinité")]
    [InlineData("1 Boul. René-Lévesque, Québec", "1 boulevard René-Lévesque, Québec")]
    [InlineData("2999 aut. 440, Laval", "2999 autoroute 440, Laval")]
    [InlineData("811 av. Taniata, Lévis", "811 avenue Taniata, Lévis")]
    [InlineData("1952 côte des Cascades, PAPINEAUVILLE", "1952 côte des Cascades, PAPINEAUVILLE")]
    public void The_address_search_gets_the_street_types_spelled_out(string address, string query) =>
        Assert.Equal(query, PositionFinder.GeocodeQuery(address));

    [Theory]
    [InlineData("1952 côte des Cascades, PAPINEAUVILLE", "papineauville")]
    [InlineData("10 rue X, St-Jérôme", "saint-jerome")]
    [InlineData("Saint Jérôme", "saint-jerome")]
    [InlineData("Ste-Julie", "sainte-julie")]
    [InlineData("Montréal", "montreal")]
    [InlineData(" , ", null)]
    [InlineData(null, null)]
    public void Towns_are_compared_folded(string? text, string? town) => Assert.Equal(town, PositionFinder.Town(text));

    [Fact]
    public void Each_day_s_run_is_at_5_30_in_Québec_time_across_daylight_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 8, 40, 0, TimeSpan.Zero)); // 3:40 EST.
        var runAt = new TimeSpan(5, 30, 0);

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 10, 30, 0, TimeSpan.Zero), PositionsWorker.NextRun(clock.GetUtcNow(), runAt));
        clock.Advance(TimeSpan.FromHours(2)); // 5:40 EST.
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 30, 0, TimeSpan.Zero), PositionsWorker.NextRun(clock.GetUtcNow(), runAt));
        // The night daylight time starts: 5:30 EDT.
        Assert.Equal(
            new DateTimeOffset(2027, 3, 14, 9, 30, 0, TimeSpan.Zero),
            PositionsWorker.NextRun(new DateTimeOffset(2027, 3, 14, 5, 0, 0, TimeSpan.Zero), runAt));
    }

    [Theory]
    [InlineData(null, "05:30:00", null)]
    [InlineData("http://geocoding:2322/", "05:30:00", null)]
    [InlineData("http://geocoding:2322/photon", "05:30:00", "Positions:GeocodingUrl")]
    [InlineData("geocoding:2322/", "05:30:00", "Positions:GeocodingUrl")]
    [InlineData("ftp://geocoding/", "05:30:00", "Positions:GeocodingUrl")]
    [InlineData(null, "1.00:00:00", "Positions:RunAt")]
    public void The_settings_are_checked_on_start(string? url, string runAt, string? error)
    {
        var options = new PositionsOptions { GeocodingUrl = url, RunAt = TimeSpan.Parse(runAt, System.Globalization.CultureInfo.InvariantCulture) };

        var message = options.Validate();

        if (error is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.StartsWith(error, message, StringComparison.Ordinal);
        }

        Assert.Equal(url is null, options.GeocodingUri is null);
    }
}
