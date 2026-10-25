namespace Essence.Ranking.Tests;

public class PlacesTests
{
    private static readonly LatLon Downtown = new(45.4988, -73.5666);

    [Fact]
    public void An_address_is_its_number_and_street_then_the_town_and_postal_code()
    {
        var label = Places.Describe(House("1000", "Rue De La Gauchetière Ouest", postcode: "H3A 4W5"));

        Assert.Equal(new PlaceLabel("1000 Rue De La Gauchetière Ouest", "Montréal, QC H3A 4W5", PlaceKinds.Address), label);
    }

    [Fact]
    public void A_named_place_is_its_name_then_its_address()
    {
        var label = Places.Describe(House("1000", "Rue de La Gauchetière Ouest", name: "Boston Consulting Group", osmKey: "office"));

        Assert.Equal(new PlaceLabel("Boston Consulting Group", "1000 Rue de La Gauchetière Ouest, Montréal", PlaceKinds.Named), label);
    }

    [Fact]
    public void A_building_named_by_its_number_is_an_address()
    {
        var label = Places.Describe(House("1000", "Rue De La Gauchetière Ouest", name: "1000"));

        Assert.Equal(PlaceKinds.Address, label!.Kind);
        Assert.Equal("1000 Rue De La Gauchetière Ouest", label.Label);
    }

    [Fact]
    public void A_street_is_its_name_then_its_town()
    {
        var label = Places.Describe(Place("street", name: "Rue Gaucher", city: "Marieville"));

        Assert.Equal(new PlaceLabel("Rue Gaucher", "Marieville", PlaceKinds.Street), label);
    }

    [Fact]
    public void A_town_is_its_name_then_its_region()
    {
        var label = Places.Describe(Place("city", name: "Sherbrooke", city: null) with { County = "Sherbrooke (TÉ)" });

        Assert.Equal(new PlaceLabel("Sherbrooke", "Sherbrooke (TÉ)", PlaceKinds.Town), label);
    }

    [Fact]
    public void A_part_of_a_town_is_its_name_then_the_town()
    {
        var label = Places.Describe(Place("district", name: "Ville-Marie"));

        Assert.Equal(new PlaceLabel("Ville-Marie", "Montréal", PlaceKinds.Town), label);
    }

    [Fact]
    public void A_place_without_a_name_or_street_has_no_label() =>
        Assert.Null(Places.Describe(Place("other", name: null)));

    [Fact]
    public void Suggestions_keep_Québec_s_places_only()
    {
        var suggestions = Places.Suggestions(
        [
            House("100", "Queen Street", city: "Ottawa", state: "Ontario"),
            House("1000", "Rue De La Gauchetière Ouest"),
            Place("street", name: "Rue Main", state: null),
        ], "1000 de la gauch");

        Assert.Equal(["1000 Rue De La Gauchetière Ouest"], suggestions.Select(s => s.Label.Label));
    }

    [Fact]
    public void An_address_entered_twice_is_suggested_once()
    {
        var suggestions = Places.Suggestions(
        [
            House("100", "Rue Principale", postcode: "J0B 3E2", at: new LatLon(45.0100, -72.0950)),
            House("100", "Rue Principale", postcode: "J0B 3E5", at: new LatLon(45.0101, -72.0951)),
            House("100", "Rue Principale", city: "Granby", at: new LatLon(45.40, -72.73)),
        ], "100 principale");

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("Montréal, QC J0B 3E2", suggestions[0].Label.Detail);
        Assert.Equal("Granby", suggestions[1].Label.Detail);
    }

    [Fact]
    public void A_street_s_stretches_are_suggested_once_per_town()
    {
        var suggestions = Places.Suggestions(
        [
            Place("street", name: "Rue Saint-Hubert", at: new LatLon(45.5352, -73.6005)),
            Place("street", name: "Rue Saint-Hubert", at: new LatLon(45.5150, -73.5690)),
            Place("street", name: "Rue Saint-Hubert", at: new LatLon(45.5550, -73.6280)),
            Place("street", name: "Rue Saint-Hubert", city: "Saint-Jérôme", at: new LatLon(45.78, -74.0)),
        ], "rue saint-hubert");

        Assert.Equal(["Montréal", "Saint-Jérôme"], suggestions.Select(s => s.Label.Detail));
        Assert.Equal(new LatLon(45.5352, -73.6005), suggestions[0].Place.Location);
    }

    [Fact]
    public void At_most_five_are_suggested_in_Photon_s_order()
    {
        var places = Enumerable.Range(1, 9).Select(n => House($"{n}", "Rue Gaucher", at: new LatLon(45 + n * 0.01, -73))).ToList();

        var suggestions = Places.Suggestions(places, "gaucher");

        Assert.Equal(["1 Rue Gaucher", "2 Rue Gaucher", "3 Rue Gaucher", "4 Rue Gaucher", "5 Rue Gaucher"], suggestions.Select(s => s.Label.Label));
    }

    [Fact]
    public void A_business_found_by_its_address_comes_after_the_address()
    {
        var suggestions = Places.Suggestions(
        [
            House("1000", "Rue De La Gauchetière Ouest", name: "1000 de la Gauchetière", osmKey: "building"),
            House("1000", "Rue de La Gauchetière Ouest", name: "Boston Consulting Group", osmKey: "office", at: new LatLon(45.4984, -73.5665)),
            House("1000", "Rue De La Gauchetière Ouest", at: new LatLon(45.4988, -73.5666)),
            House("1000", "Rue De La Gauchetière Est", at: new LatLon(45.5153, -73.5537)),
        ], "1000 de la gauch");

        Assert.Equal(
            ["1000 de la Gauchetière", "1000 Rue De La Gauchetière Ouest", "1000 Rue De La Gauchetière Est", "Boston Consulting Group"],
            suggestions.Select(s => s.Label.Label));
    }

    [Fact]
    public void Businesses_at_the_number_typed_come_after_the_address_whatever_their_names()
    {
        string[] tenants = ["Banque Laurentienne", "Deloitte", "Lavery Avocats", "Dentisterie Centre-Ville", "Café Dépôt"];
        var suggestions = Places.Suggestions(
        [
            .. tenants.Select((name, i) => House("1000", "Rue De La Gauchetière Ouest", name: name, osmKey: "office",
                at: new LatLon(45.4984 + i * 0.00001, -73.5665))),
            House("1000", "Rue De La Gauchetière Ouest", at: new LatLon(45.4988, -73.5666)),
        ], "1000 de la gauch");

        Assert.Equal(["1000 Rue De La Gauchetière Ouest", .. tenants[..4]], suggestions.Select(s => s.Label.Label));
    }

    [Fact]
    public void A_business_found_by_its_name_keeps_its_place()
    {
        var suggestions = Places.Suggestions(
        [
            House("1000", "Rue De La Gauchetière Ouest", name: "Deloitte", osmKey: "office"),
            House("1000", "Rue De La Gauchetière Ouest", at: new LatLon(45.4988, -73.5666)),
        ], "deloitte montreal");

        Assert.Equal(["Deloitte", "1000 Rue De La Gauchetière Ouest"], suggestions.Select(s => s.Label.Label));
    }

    [Fact]
    public void A_pin_far_from_any_address_is_named_by_the_road_it_s_on()
    {
        var pin = new LatLon(46.3000, -72.9000);

        var nearest = Places.Nearest(pin,
        [
            House("1234", "Rang Saint-Joseph", city: "Saint-Barnabé", at: new LatLon(46.3036, -72.9000)),
        ]);

        Assert.Equal(new PlaceLabel("Rang Saint-Joseph", "Saint-Barnabé", PlaceKinds.Street), nearest!.Value.Label);
    }

    [Fact]
    public void A_pin_is_named_by_the_nearest_address_within_100_m()
    {
        var nearest = Places.Nearest(Downtown,
        [
            Place("street", name: "Boulevard René-Lévesque Ouest", at: new LatLon(45.4988, -73.5667)),
            House("1000", "Rue De La Gauchetière Ouest", at: new LatLon(45.4993, -73.5666)),
        ]);

        Assert.Equal("1000 Rue De La Gauchetière Ouest", nearest!.Value.Label.Label);
    }

    [Fact]
    public void Without_an_address_close_by_a_pin_is_named_by_the_nearest_place()
    {
        var nearest = Places.Nearest(Downtown,
        [
            Place("street", name: "Boulevard René-Lévesque Ouest", at: new LatLon(45.4988, -73.5667)),
            House("1000", "Rue De La Gauchetière Ouest", at: new LatLon(45.5040, -73.5666)),
        ]);

        Assert.Equal("Boulevard René-Lévesque Ouest", nearest!.Value.Label.Label);
    }

    [Fact]
    public void Nothing_near_a_pin_names_it_nothing() =>
        Assert.Null(Places.Nearest(Downtown, []));

    [Theory]
    [InlineData("Québec", "quebec")]
    [InlineData("  Rue   De La  GAUCHETIÈRE ", "rue de la gauchetiere")]
    [InlineData("Île-d'Orléans", "ile-d'orleans")]
    public void Folding_drops_case_accents_and_extra_spaces(string text, string folded) =>
        Assert.Equal(folded, Places.Fold(text));

    private static PhotonPlace House(
        string number, string street, string? name = null, string? osmKey = "place", string? postcode = null, string? city = "Montréal",
        string? state = "Québec", LatLon? at = null) =>
        new(at ?? Downtown, "house", osmKey, "house", name, number, street, postcode, null, "Ville-Marie", city,
            "Agglomération de Montréal", state, "CA");

    private static PhotonPlace Place(string type, string? name, string? city = "Montréal", string? state = "Québec", LatLon? at = null) =>
        new(at ?? Downtown, type, "highway", "residential", name, null, null, null, null, null, city, null, state, "CA");
}
