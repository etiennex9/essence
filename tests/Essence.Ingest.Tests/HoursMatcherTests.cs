using Essence.Data;
using Essence.Data.Entities;
using Essence.Ingest.Hours;

namespace Essence.Ingest.Tests;

public sealed class HoursMatcherTests
{
    private const string Week = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";
    private const double CostcoRadius = 600;

    /// <summary>
    /// Ultramar's, Shell's and OpenStreetMap's radius, and OpenStreetMap's reach for an element of another brand on a
    /// station (<see cref="OverpassSource"/>).
    /// </summary>
    private const double Radius = 150;
    private const double OsmOtherBrandReach = 50;

    // The feed's Costco stations and Costco's warehouses, as both gave them on 2026-11-12.
    private static readonly MatchStation LavalStation = Station(1, "2999 aut. 440, Laval", "H7P 5P4", "Costco", 45.574157, -73.756979);
    private static readonly MatchStation DrummondvilleStation = Station(2, "105 rue Robert-Bernard, Drummondville", "J2C 8N1", "Costco", 45.909749, -72.509487);
    private static readonly MatchStation AnjouStation = Station(3, "9403 boul. des Sciences, Montréal", "H1J 0A6", "Costco", 45.623187, -73.5551);
    private static readonly SourceRecord LavalWarehouse = Warehouse("505", "2999 AUTOROUTE LAVAL 440 OUEST", "H7P 5P4", 45.574, -73.757);
    private static readonly SourceRecord DrummondvilleWarehouse = Warehouse("1127", "105 RUE ROBERT-BERNARD", "J2C 0P6", 45.908, -72.516);
    private static readonly SourceRecord AnjouWarehouse = Warehouse("1446", "9401 BOULEVARD DES SCIENCES", "H1J 0A6", 45.623, -73.558);

    [Fact]
    public void Costco_Laval_matches_its_warehouse_confirmed_by_its_postal_code_and_number()
    {
        var result = HoursMatcher.Match([LavalWarehouse], [LavalStation], CostcoRadius);

        var match = Assert.Single(result.Matches);
        Assert.Equal(new HoursMatch(1, LavalWarehouse, StationHoursEntry.ByDistance, 18, Confirmed: true), match);
        Assert.Equal(0, result.Unmatched);
    }

    [Fact]
    public void Costco_s_pumps_540_m_from_the_warehouse_match_by_the_house_number_and_another_postal_code_by_the_postal_code()
    {
        var result = HoursMatcher.Match([DrummondvilleWarehouse, AnjouWarehouse], [DrummondvilleStation, AnjouStation], CostcoRadius);

        Assert.Collection(
            result.Matches.OrderBy(m => m.StationId),
            m => Assert.Equal((2L, "1127", 540, true), (m.StationId, m.Record.Ref, m.DistanceM!.Value, m.Confirmed)),
            m => Assert.Equal((3L, "1446", 226, true), (m.StationId, m.Record.Ref, m.DistanceM!.Value, m.Confirmed)));
    }

    [Fact]
    public void Beyond_the_radius_only_a_station_at_the_same_number_and_postal_code_matches()
    {
        var far = LavalStation with { Latitude = 45.574157 + 0.0055 }; // About 610 m north.
        var elsewhere = far with { HouseNumber = "3000" };

        var atItsAddress = HoursMatcher.Match([LavalWarehouse], [far], CostcoRadius);
        var result = HoursMatcher.Match([LavalWarehouse], [elsewhere], CostcoRadius);

        var match = Assert.Single(atItsAddress.Matches);
        Assert.Equal((StationHoursEntry.ByAddress, true), (match.MatchedBy, match.Confirmed));
        Assert.Empty(result.Matches);
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public void A_lone_station_within_reach_matches_unconfirmed_when_neither_its_postal_code_nor_its_number_agree()
    {
        var elsewhere = LavalStation with { HouseNumber = "3000", Postcode = "H7P0A1" };

        var match = Assert.Single(HoursMatcher.Match([LavalWarehouse], [elsewhere], CostcoRadius).Matches);

        Assert.False(match.Confirmed);
    }

    [Fact]
    public void Different_brands_never_match_however_close()
    {
        var shell = LavalStation with { BrandFamily = "shell" };
        var noBrand = LavalStation with { Id = 9, BrandFamily = null };

        var result = HoursMatcher.Match([LavalWarehouse], [shell, noBrand], CostcoRadius);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Each_station_gets_one_record_and_each_record_one_station_the_confirmed_first()
    {
        // Record A is closest to station 2 but confirmed with station 1; record B is near station 2 only.
        var stationOne = Station(1, "100 rue A", "H1A 1A1", "Costco", 45.5000, -73.5000);
        var stationTwo = Station(2, "200 rue B", "H2B 2B2", "Costco", 45.5020, -73.5000);
        var recordA = Warehouse("A", "100 RUE A", "H1A 1A1", 45.5015, -73.5000);
        var recordB = Warehouse("B", "999 RUE Z", "H9Z 9Z9", 45.5030, -73.5000) with { Rule = "24/7" };

        var result = HoursMatcher.Match([recordA, recordB], [stationOne, stationTwo], CostcoRadius);

        Assert.Equal([(1L, "A", true), (2L, "B", false)], result.Matches.OrderBy(m => m.StationId).Select(m => (m.StationId, m.Record.Ref, m.Confirmed)));
    }

    [Fact]
    public void A_record_within_reach_of_two_stations_that_neither_confirms_matches_neither()
    {
        var east = Station(1, "100 rue A", "H1A 1A1", "Costco", 45.5000, -73.5000);
        var west = Station(2, "200 rue B", "H2B 2B2", "Costco", 45.5000, -73.5050);
        var record = Warehouse("A", "999 RUE Z", "H9Z 9Z9", 45.5000, -73.5010);

        var result = HoursMatcher.Match([record], [east, west], CostcoRadius);

        Assert.Empty(result.Matches);
        Assert.Equal((1, 1), (result.Unmatched, result.Ambiguous));
    }

    [Fact]
    public void Two_records_with_other_hours_near_one_station_that_neither_confirms_match_neither()
    {
        var station = Station(1, "100 rue A", "H1A 1A1", "Costco", 45.5000, -73.5000);
        var first = Warehouse("A", "999 RUE Z", "H9Z 9Z9", 45.5010, -73.5000);
        var other = Warehouse("B", "998 RUE Z", "H9Z 9Z8", 45.5020, -73.5000) with { Rule = "24/7" };
        var same = other with { Rule = Week };

        var disagree = HoursMatcher.Match([first, other], [station], CostcoRadius);
        var agree = HoursMatcher.Match([first, same], [station], CostcoRadius);

        Assert.Empty(disagree.Matches);
        Assert.Equal(2, disagree.Ambiguous);
        Assert.Equal("A", Assert.Single(agree.Matches).Record.Ref);
    }

    [Fact]
    public void A_confirmed_match_settles_what_was_ambiguous()
    {
        // The record would be ambiguous between the two stations, but station 2 is confirmed with another record.
        var east = Station(1, "100 rue A", "H1A 1A1", "Costco", 45.5000, -73.5000);
        var west = Station(2, "200 rue B", "H2B 2B2", "Costco", 45.5000, -73.5050);
        var record = Warehouse("A", "999 RUE Z", "H9Z 9Z9", 45.5000, -73.5010);
        var westRecord = Warehouse("B", "200 RUE B", "H2B 2B2", 45.5000, -73.5060);

        var result = HoursMatcher.Match([record, westRecord], [east, west], CostcoRadius);

        Assert.Equal([(1L, "A"), (2L, "B")], result.Matches.OrderBy(m => m.StationId).Select(m => (m.StationId, m.Record.Ref)));
    }

    [Fact]
    public void A_station_s_curated_rule_beats_its_brand_s_and_the_brand_s_reaches_every_other_station_of_the_brand()
    {
        var brandRule = new SourceRecord("brand/costco", BrandFamily.Costco, null, null, null, null, Week, null, null, false);
        var stationRule = new SourceRecord("station/laval", null, null, null, null, null, "Mo-Su 06:00-22:00", null, null, false)
        {
            NaturalKey = LavalStation.NaturalKey,
        };
        var shell = Station(4, "1 rue Shell", "H4A 4A4", "Shell", 45.5, -73.6);

        var result = HoursMatcher.Match([brandRule, stationRule], [LavalStation, DrummondvilleStation, shell], 0);

        Assert.Equal(
            [(1L, "Mo-Su 06:00-22:00", StationHoursEntry.ByNaturalKey), (2L, Week, StationHoursEntry.ByBrand)],
            result.Matches.OrderBy(m => m.StationId).Select(m => (m.StationId, m.Record.Rule!, m.MatchedBy)));
        Assert.All(result.Matches, m => Assert.True(m.Confirmed));
    }

    [Fact]
    public void A_station_rule_for_a_station_not_in_the_feed_is_unmatched()
    {
        var stationRule = new SourceRecord("station/gone", null, null, null, null, null, Week, null, null, false)
        {
            NaturalKey = StationKey.Create("1 rue Disparue", "G0A 0A0"),
        };

        var result = HoursMatcher.Match([stationRule], [LavalStation], 0);

        Assert.Empty(result.Matches);
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public void Couche_Tard_s_store_matches_the_station_named_after_it_whatever_its_brand_and_700_m_away()
    {
        // The feed's "Couche-Tard #140" sells Esso's gas; the store's point is moved 700 m east.
        var station = Station(10, "45 boul. Saint-Elzéar O., Laval", "H7M 1E3", "Esso", 45.600770, -73.732023) with { StoreNumber = 140 };
        var store = Store(140, 45.600740, -73.732050 + 0.009);

        var match = Assert.Single(HoursMatcher.Match([store], [station], 0).Matches);

        Assert.Equal((10L, StationHoursEntry.ByStoreNumber, true), (match.StationId, match.MatchedBy, match.Confirmed));
        Assert.InRange(match.DistanceM!.Value, 690, 710);
    }

    [Fact]
    public void A_Couche_Tard_store_121_m_from_a_Shell_isn_t_matched_to_it_nor_to_a_Couche_Tard_not_named_after_it()
    {
        var shell = Station(11, "1 rue Principale, Laval", "H7M 1E3", "Shell", 45.6000, -73.7320);
        var coucheTard = Station(12, "2 rue Principale, Laval", "H7M 1E3", "Couche-Tard", 45.6001, -73.7321);
        var store = Store(999, 45.6000 + 0.00109, -73.7320) with { HouseNumber = "1", Postcode = "H7M1E3" };

        var result = HoursMatcher.Match([store], [shell, coucheTard], 150);

        Assert.Empty(result.Matches);
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public void A_store_number_two_stations_carry_matches_neither_and_a_store_without_a_point_is_never_its_brand_s_rule()
    {
        var first = Station(13, "1 rue A, Laval", "H7M 1E3", "Esso", 45.60, -73.73) with { StoreNumber = 140 };
        var second = Station(14, "2 rue B, Laval", "H7M 1E4", "Irving", 45.61, -73.74) with { StoreNumber = 140 };
        var other = Station(15, "3 rue C, Laval", "H7M 1E5", "Couche-Tard", 45.62, -73.75);
        var store = Store(140, null, null);

        var result = HoursMatcher.Match([store], [first, second, other], 150);

        Assert.Empty(result.Matches);
        Assert.Equal((1, 1), (result.Unmatched, result.Ambiguous));
    }

    [Theory]
    [InlineData("Couche-Tard #140", 140)]
    [InlineData("Couche-Tard # 140", 140)]
    [InlineData("COUCHE-TARD #140", 140)]
    [InlineData("  Couche Tard #1322 ", 1322)]
    [InlineData("Couche-Tard inc.", null)]
    [InlineData("COUCHE-TARD INC", null)]
    [InlineData("Couche-Tard #0", null)]
    [InlineData("Dépanneur Couche-Tard #140 et lave-auto", null)]
    [InlineData("2642-4101 Québec Inc.", null)]
    [InlineData(null, null)]
    public void A_station_s_store_number_is_the_one_its_name_ends_with(string? name, int? number)
    {
        Assert.Equal(number, HoursMatcher.StoreNumber(name));
    }

    [Theory]
    [InlineData("ultramar")]
    [InlineData("shell")]
    [InlineData("petro-canada")]
    public void A_brand_s_record_matches_its_brand_s_station_never_the_other_brand_s_closer_one(string family)
    {
        var record = new SourceRecord("node/1", family, 45.5000, -73.5000, null, null, "24/7", null, null, false);
        var other = Station(20, "1 rue A", "H1A 1A1", family == "shell" ? "Esso" : "Shell", 45.5001, -73.5000);
        var own = new MatchStation(21, "own", family, 45.5010, -73.5000, "2", "H1A1A2");

        var match = Assert.Single(HoursMatcher.Match([record], [other, own], 150).Matches);

        Assert.Equal((21L, false), (match.StationId, match.Confirmed));
    }

    [Fact]
    public void Two_stations_of_the_brand_within_reach_of_a_record_that_confirms_neither_match_neither_and_one_beyond_150_m_never()
    {
        var record = new SourceRecord("node/1", "ultramar", 45.5000, -73.5000, "999", "H9Z9Z9", "Mo-Su 06:00-22:00", null, null, false);
        var east = new MatchStation(1, "east", "ultramar", 45.5000, -73.4990, "1", "H1A1A1");
        var west = new MatchStation(2, "west", "ultramar", 45.5000, -73.5010, "2", "H1A1A2");
        // About 156 m north, at the record's postal code but not its number.
        var far = new MatchStation(3, "far", "ultramar", 45.5014, -73.5000, "998", "H9Z9Z9");

        var both = HoursMatcher.Match([record], [east, west], 150);
        var beyond = HoursMatcher.Match([record], [far], 150);

        Assert.Empty(both.Matches);
        Assert.Equal(1, both.Ambiguous);
        Assert.Empty(beyond.Matches);
    }

    [Fact]
    public void OpenStreetMap_s_Costco_pumps_match_Costco_Laval_188_m_away_as_Costco_s_own_records_do()
    {
        var pumps = new SourceRecord("node/6756481766", BrandFamily.Costco, 45.575341, -73.7552574, null, null, null,
            "https://www.openstreetmap.org/node/6756481766", null, false);
        var shellPumps = pumps with { Ref = "node/2", BrandFamily = "shell" };
        var shell = LavalStation with { Id = 9, BrandFamily = "shell" };

        var costco = Assert.Single(HoursMatcher.Match([pumps], [LavalStation], 150).Matches);
        var other = HoursMatcher.Match([shellPumps], [shell], 150);

        Assert.Equal((1L, 188), (costco.StationId, costco.DistanceM!.Value));
        Assert.Empty(other.Matches);
    }

    [Theory]
    [InlineData("2999 aut. 440, Laval", "2999")]
    [InlineData("  0105 rue Robert-Bernard", "105")]
    [InlineData("9403A boul. des Sciences", "9403")]
    [InlineData("rue Principale", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_house_number_is_the_number_an_address_starts_with(string? address, string? number)
    {
        Assert.Equal(number, HoursMatcher.HouseNumber(address));
    }

    [Theory]
    [InlineData("Costco", "costco")]
    [InlineData("COSTCO", "costco")]
    [InlineData("Costco Gasoline", "costco")]
    [InlineData("Esso", "esso")]
    [InlineData("ESSO", "esso")]
    [InlineData("Mobil", "esso")]
    [InlineData("Esso Express", "esso")]
    [InlineData("Couche-Tard", "couche-tard")]
    [InlineData("Couche Tard", "couche-tard")]
    [InlineData("Circle K", "couche-tard")]
    [InlineData("CIRCLEK", "couche-tard")]
    [InlineData("Petro-Canada", "petro-canada")]
    [InlineData("Pétro-Canada", "petro-canada")]
    [InlineData("Petro Canada", "petro-canada")]
    [InlineData("PetroCanada", "petro-canada")]
    [InlineData("Ultramar", "ultramar")]
    [InlineData("Shell", "shell")]
    [InlineData("Irving", "irving")]
    [InlineData("Irving Oil", "irving")]
    [InlineData("Harnois Énergies", "harnois")]
    [InlineData("Sonic", "sonic")]
    [InlineData("Crevier", "crevier")]
    [InlineData("Canadian Tire", "canadian-tire")]
    [InlineData("Essence+ de Canadian Tire", "canadian-tire")]
    [InlineData("Pétro-T", "harnois")]
    [InlineData("Petro T", "harnois")]
    [InlineData("PetroT", "harnois")]
    [InlineData("Nutrinor Énergies", "nutrinor-energies")]
    [InlineData("R.L.", "r-l")]
    [InlineData("Petroles RL", "r-l")]
    [InlineData("Pêtroles RL", "r-l")]
    [InlineData("Pétrole Rl", "r-l")]
    [InlineData("Pétroles R.L.", "r-l")]
    [InlineData("Rezzo", "r-l")]
    [InlineData("Aucun", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_brand_s_family_folds_accents_case_and_the_names_of_one_network(string? brand, string? family)
    {
        Assert.Equal(family, BrandFamily.Of(brand));
    }

    [Theory]
    [InlineData(1_900, true)]
    [InlineData(2_100, false)]
    public void A_record_too_far_for_its_station_matches_it_by_address_when_both_its_number_and_postal_code_agree_within_2_km(int metres, bool matches)
    {
        var station = Station(1, "100 rue Principale, Stoke", "J0B 3G0", "Ultramar", 45.5, -71.8);
        var record = Ultramar("2037", "100 PRINCIPALE", "J0B 3G0", North(45.5, metres), -71.8);

        var result = HoursMatcher.Match([record], [station], Radius);

        if (matches)
        {
            Assert.Equal(new HoursMatch(1, record, StationHoursEntry.ByAddress, metres, Confirmed: true), Assert.Single(result.Matches));
            Assert.Equal((0, 0, 1, 1), (result.Unmatched, result.Ambiguous, result.ByAddress, result.Counted));
        }
        else
        {
            Assert.Empty(result.Matches);
            Assert.Equal(1, result.Unmatched);
        }
    }

    [Theory]
    [InlineData("100 PRINCIPALE", "J0B 3G1", "ultramar")] // The number only.
    [InlineData("102 PRINCIPALE", "J0B 3G0", "ultramar")] // The postal code only: a rural one covers villages.
    [InlineData("100 PRINCIPALE", "J0B 3G0", "shell")]
    [InlineData("100 PRINCIPALE", null, "ultramar")]
    public void Neither_the_number_nor_the_postal_code_alone_nor_another_brand_s_record_matches_by_address(string address, string? postalCode, string family)
    {
        var station = Station(1, "100 rue Principale, Stoke", "J0B 3G0", "Ultramar", 45.5, -71.8);
        var record = Ultramar("2037", address, postalCode, North(45.5, 400), -71.8) with { BrandFamily = family };

        Assert.Empty(HoursMatcher.Match([record], [station], Radius).Matches);
    }

    [Fact]
    public void A_record_at_the_address_of_two_stations_or_two_records_at_one_station_s_match_none_by_address()
    {
        // In one rural postal code, 100 rue Principale and 100 route 112.
        var village = Station(1, "100 rue Principale, Stoke", "J0B 3G0", "Ultramar", 45.5, -71.8);
        var road = Station(2, "100 route 112, Stoke", "J0B 3G0", "Ultramar", North(45.5, 1_500), -71.8);
        var record = Ultramar("2037", "100 PRINCIPALE", "J0B 3G0", North(45.5, 700), -71.8);
        var other = Ultramar("2038", "100 RTE 112", "J0B 3G0", North(45.5, -700), -71.8) with { Rule = "Mo-Su 06:00-22:00" };

        var twoStations = HoursMatcher.Match([record], [village, road], Radius);
        var twoRecords = HoursMatcher.Match([record, other], [village], Radius);

        Assert.Empty(twoStations.Matches);
        Assert.Equal((1, 1), (twoStations.Unmatched, twoStations.Ambiguous));
        Assert.Empty(twoRecords.Matches);
        Assert.Equal((2, 2), (twoRecords.Unmatched, twoRecords.Ambiguous));
    }

    [Fact]
    public void Only_what_the_match_by_distance_left_free_matches_by_address_one_to_one()
    {
        // Station 1 has its record 30 m away; another at its address, 1 km away, is a duplicate that finds nothing. Station
        // 2's record, 900 m away, is at its address.
        var first = Station(1, "100 rue Principale, Stoke", "J0B 3G0", "Ultramar", 45.5, -71.8);
        var second = Station(2, "381 rue Principale, Stoke", "J0B 3G0", "Ultramar", North(45.5, 3_000), -71.8);
        var near = Ultramar("1", "100 PRINCIPALE", "J0B 3G0", North(45.5, 30), -71.8);
        var duplicate = Ultramar("2", "100 PRINCIPALE", "J0B 3G0", North(45.5, -1_000), -71.8);
        var off = Ultramar("3", "381 PRINCIPALE", "J0B 3G0", North(45.5, 2_100), -71.8);

        var result = HoursMatcher.Match([near, duplicate, off], [first, second], Radius);

        Assert.Equal(
            [(1L, "1", StationHoursEntry.ByDistance), (2L, "3", StationHoursEntry.ByAddress)],
            result.Matches.OrderBy(m => m.StationId).Select(m => (m.StationId, m.Record.Ref, m.MatchedBy)));
        Assert.Equal((1, 0), (result.Unmatched, result.Ambiguous));
    }

    [Fact]
    public void A_Nutrinor_station_20_m_from_an_element_OpenStreetMap_still_calls_Sonic_gets_it_by_location_without_its_hours()
    {
        var station = Station(1, "100 boul. Saint-Paul, Saguenay", "G7J 3C5", "Nutrinor Énergies", 48.4, -71.1);
        var sonic = Element("way/1", "Sonic", North(48.4, 20), -71.1) with
        {
            Rule = "Mo-Su 06:00-23:00",
            CheckedAt = new DateTimeOffset(2014, 6, 26, 4, 0, 0, TimeSpan.Zero),
        };

        var result = HoursMatcher.Match([sonic], [station], Radius, OsmOtherBrandReach);

        var match = Assert.Single(result.Matches);
        Assert.Equal(
            new HoursMatch(1, sonic with { Rule = null, CheckedAt = null }, StationHoursEntry.ByLocation, 20, Confirmed: false),
            match);
        Assert.Equal("https://www.openstreetmap.org/way/1", match.Record.Link);
        Assert.Equal((0, 1, 0), (result.Unmatched, result.ByLocation, result.Counted));
        // A brand's locator lists only its own stations: no match by location.
        Assert.Empty(HoursMatcher.Match([sonic], [station], Radius).Matches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ultramar")]
    public void An_element_with_no_brand_or_another_on_the_station_is_its_by_location(string? brand)
    {
        var station = Station(1, "1 rue Principale, Laval", "H7M 1E3", "Esso", 45.6, -73.7);
        var element = Element("node/1", brand, North(45.6, 45), -73.7);

        var match = Assert.Single(HoursMatcher.Match([element], [station], Radius, OsmOtherBrandReach).Matches);

        Assert.Equal((StationHoursEntry.ByLocation, 45), (match.MatchedBy, match.DistanceM!.Value));
    }

    [Fact]
    public void An_element_60_m_from_the_station_isn_t_its_by_location()
    {
        var station = Station(1, "1 rue Principale, Laval", "H7M 1E3", "Esso", 45.6, -73.7);

        var result = HoursMatcher.Match([Element("node/1", null, North(45.6, 60), -73.7)], [station], Radius, OsmOtherBrandReach);

        Assert.Empty(result.Matches);
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public void An_element_within_150_m_of_another_feed_station_matched_or_not_is_no_one_s_by_location()
    {
        var nutrinor = Station(1, "100 boul. Saint-Paul, Saguenay", "G7J 3C5", "Nutrinor Énergies", 48.4, -71.1);
        var sonic = Element("way/1", "Sonic", North(48.4, 20), -71.1);
        // A Petro-Canada 120 m from the Sonic element, with its own element 5 m away, and an Esso 140 m away with none.
        var petroCanada = Station(2, "120 boul. Saint-Paul, Saguenay", "G7J 3C5", "Petro-Canada", North(48.4, 140), -71.1);
        var own = Element("node/2", "Petro-Canada", North(48.4, 145), -71.1);
        var esso = Station(3, "140 boul. Saint-Paul, Saguenay", "G7J 3C5", "Esso", North(48.4, -120), -71.1);

        var matched = HoursMatcher.Match([sonic, own], [nutrinor, petroCanada], Radius, OsmOtherBrandReach);
        var unmatched = HoursMatcher.Match([sonic], [nutrinor, esso], Radius, OsmOtherBrandReach);

        var petroCanadaMatch = Assert.Single(matched.Matches);
        Assert.Equal((2L, StationHoursEntry.ByDistance), (petroCanadaMatch.StationId, petroCanadaMatch.MatchedBy));
        Assert.Empty(unmatched.Matches);
    }

    [Fact]
    public void An_element_of_the_station_s_brand_within_150_m_wins_over_one_of_another_brand_on_it()
    {
        var station = Station(1, "100 boul. Saint-Paul, Saguenay", "G7J 3C5", "Nutrinor Énergies", 48.4, -71.1);
        var sonic = Element("way/1", "Sonic", North(48.4, 20), -71.1);
        var nutrinor = Element("node/2", "Nutrinor Énergies", North(48.4, -120), -71.1);

        var result = HoursMatcher.Match([sonic, nutrinor], [station], Radius, OsmOtherBrandReach);

        var match = Assert.Single(result.Matches);
        Assert.Equal(("node/2", StationHoursEntry.ByDistance), (match.Record.Ref, match.MatchedBy));
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public void An_element_already_matched_to_its_own_station_isn_t_another_s_by_location()
    {
        // The element is a Sonic's, at its address 1 km away, the Sonic's point being off; a Nutrinor is 20 m from it.
        var sonicStation = Station(1, "10 rue Racine, Saguenay", "G7H 1A1", "Sonic", North(48.4, 1_000), -71.1);
        var nutrinor = Station(2, "12 rue Racine, Saguenay", "G7H 1A1", "Nutrinor Énergies", 48.4, -71.1);
        var element = Element("node/1", "Sonic", North(48.4, 20), -71.1) with { HouseNumber = "10", Postcode = "G7H1A1" };

        var match = Assert.Single(HoursMatcher.Match([element], [sonicStation, nutrinor], Radius, OsmOtherBrandReach).Matches);

        Assert.Equal((1L, StationHoursEntry.ByAddress), (match.StationId, match.MatchedBy));
    }

    [Fact]
    public void An_element_of_the_station_s_brand_left_out_by_distance_isn_t_taken_by_location_either()
    {
        // Two Esso elements with other hours, neither confirmed: which is the station's can't be told.
        var station = Station(1, "1 rue Principale, Laval", "H7M 1E3", "Esso", 45.6, -73.7);
        var near = Element("node/1", "Esso", North(45.6, 20), -73.7) with { Rule = "24/7" };
        var other = Element("node/2", "Esso", North(45.6, -100), -73.7) with { Rule = "Mo-Su 06:00-22:00" };

        var result = HoursMatcher.Match([near, other], [station], Radius, OsmOtherBrandReach);

        Assert.Empty(result.Matches);
        Assert.Equal((2, 2), (result.Unmatched, result.Ambiguous));
    }

    [Fact]
    public void Two_free_elements_within_50_m_of_a_station_are_neither_its_by_location()
    {
        var station = Station(1, "1 rue Principale, Laval", "H7M 1E3", "Esso", 45.6, -73.7);
        var node = Element("node/1", null, North(45.6, 10), -73.7);
        var way = Element("way/2", "Ultramar", North(45.6, -40), -73.7);

        var result = HoursMatcher.Match([node, way], [station], Radius, OsmOtherBrandReach);

        Assert.Empty(result.Matches);
        Assert.Equal((2, 2), (result.Unmatched, result.Ambiguous));
    }

    [Fact]
    public void Saguenay_s_Nutrinor_on_boulevard_Saint_Paul_and_the_Petro_Canada_54_m_from_its_element_stay_unmatched()
    {
        // As the feed and OpenStreetMap had them on 2026-11-13: the Nutrinor's element, last edited in 2014 (no brand nor
        // operator, only the name "Dépanneur Sonic", which is no feed station's brand, so not kept), is 62 m from it and
        // 54 m from the Petro-Canada, which has an element without a brand 29 m away and 110 m from the Nutrinor.
        var nutrinor = Station(2427, "1360 boul. Saint-Paul, Saguenay", "G7J 3C5", "Nutrinor Énergies", 48.407448, -71.094182);
        var petroCanada = Station(2389, "1312 boul. Saint-Paul, Saguenay", "G7J 3C5", "Petro-Canada", 48.408178, -71.093216);
        SourceRecord[] elements = [Element("way/221282039", null, 48.4077161, -71.093443), Element("way/221282040", null, 48.4079935, -71.0929333)];

        var result = HoursMatcher.Match(elements, [nutrinor, petroCanada], Radius, OsmOtherBrandReach);

        Assert.Empty(result.Matches);
        Assert.Equal(2, result.Unmatched);
    }

    /// <summary>The latitude <paramref name="metres"/> north (south when negative), along the meridian.</summary>
    private static double North(double latitude, double metres) => latitude + double.RadiansToDegrees(metres / 6_371_008.8);

    private static SourceRecord Element(string reference, string? brand, double latitude, double longitude) =>
        new(reference, BrandFamily.Of(brand), latitude, longitude, null, null, null, OverpassSource.ElementLink + reference, null, false);

    private static SourceRecord Ultramar(string id, string address, string? postalCode, double latitude, double longitude) =>
        new(id, BrandFamily.Ultramar, latitude, longitude, HoursMatcher.HouseNumber(address),
            postalCode is null ? null : StationKey.NormalizePostalCode(postalCode), "24/7", UltramarSource.Link, null, false);

    private static SourceRecord Store(int number, double? latitude, double? longitude) =>
        new((CircleKSource.StoreNumberBase + number).ToString(System.Globalization.CultureInfo.InvariantCulture), BrandFamily.CoucheTard,
            latitude, longitude, null, null, "24/7", CircleKSource.Link, null, false)
        {
            StoreNumber = number,
        };

    private static MatchStation Station(long id, string address, string postalCode, string brand, double latitude, double longitude) =>
        new(id, StationKey.Create(address, postalCode), BrandFamily.Of(brand), latitude, longitude, HoursMatcher.HouseNumber(address),
            StationKey.NormalizePostalCode(postalCode));

    private static SourceRecord Warehouse(string id, string address, string postalCode, double latitude, double longitude) =>
        new(id, BrandFamily.Costco, latitude, longitude, HoursMatcher.HouseNumber(address), StationKey.NormalizePostalCode(postalCode),
            Week, CostcoSource.Link, null, false);
}
