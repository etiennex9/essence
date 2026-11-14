using Essence.Data;
using Essence.Data.Entities;
using Essence.Ingest.Hours;

namespace Essence.Ingest.Tests;

public sealed class HoursMatcherTests
{
    private const string Week = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";
    private const double CostcoRadius = 600;

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
    public void Nothing_matches_beyond_the_radius()
    {
        var far = LavalStation with { Latitude = 45.574157 + 0.0055 }; // About 610 m north.

        var result = HoursMatcher.Match([LavalWarehouse], [far], CostcoRadius);

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
        var far = new MatchStation(3, "far", "ultramar", 45.5014, -73.5000, "999", "H9Z9Z9"); // About 156 m north.

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
    [InlineData("Aucun", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_brand_s_family_folds_accents_case_and_the_names_of_one_network(string? brand, string? family)
    {
        Assert.Equal(family, BrandFamily.Of(brand));
    }

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
