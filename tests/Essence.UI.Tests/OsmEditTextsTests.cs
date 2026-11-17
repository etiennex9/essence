using System.Globalization;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Essence.UI.Tests;

/// <summary>
/// What the hours editor says of the OpenStreetMap element a correction goes to, in en-CA and fr-CA: its name, the note
/// under "Also set its brand", and the changeset's comment.
/// </summary>
public sealed class OsmEditTextsTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    private readonly StringLocalizer<Strings> _l =
        new(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Theory]
    [InlineData("en-CA", "unnamed station")]
    [InlineData("fr-CA", "station sans nom")]
    public void The_element_is_named_by_its_name_else_its_brand_else_as_unnamed(string culture, string unnamed)
    {
        Culture(culture);

        Assert.Equal("Dépanneur Sonic", OsmEditTexts.ElementName(_l, Tags(("name", "Dépanneur Sonic"), ("brand", "Sonic"))));
        Assert.Equal("Sonic", OsmEditTexts.ElementName(_l, Tags(("brand", "Sonic"))));
        Assert.Equal(unnamed, OsmEditTexts.ElementName(_l, Tags()));
    }

    [Theory]
    [InlineData("en-CA", "OpenStreetMap says Sonic.", "OpenStreetMap has no brand for it.",
        "Only for what you know first-hand: OpenStreetMap doesn’t allow Google Maps or Street View as a source.")]
    [InlineData("fr-CA", "OpenStreetMap indique Sonic.", "OpenStreetMap ne lui donne aucune marque.",
        "Seulement pour ce que vous savez de première main : OpenStreetMap n’accepte ni Google Maps ni Street View comme source.")]
    public void The_brand_s_note_says_what_OpenStreetMap_says_now_and_that_it_s_only_for_what_one_knows_first_hand(
        string culture, string says, string none, string firstHand)
    {
        Culture(culture);

        Assert.Equal($"{says} {firstHand}", OsmEditTexts.BrandNote(_l, Tags(("name", "Dépanneur Sonic"), ("brand", "Sonic"))));
        Assert.Equal($"{none} {firstHand}", OsmEditTexts.BrandNote(_l, Tags(("name", "Dépanneur Sonic"))));
        Assert.Equal($"{none} {firstHand}", OsmEditTexts.BrandNote(_l, Tags(("name", "Dépanneur Sonic")), osmBrand: null));
        Assert.Equal(firstHand, OsmEditTexts.BrandNote(_l, null));
        Assert.Equal(firstHand, OsmEditTexts.BrandNote(_l, null, osmBrand: "Sonic"));
    }

    [Theory]
    [InlineData("en-CA", "OpenStreetMap names it Sonic.", "OpenStreetMap says Ultramar.", "OpenStreetMap has no brand for it.")]
    [InlineData("fr-CA", "OpenStreetMap l’appelle Sonic.", "OpenStreetMap indique Ultramar.", "OpenStreetMap ne lui donne aucune marque.")]
    public void An_element_without_a_brand_tag_named_after_the_brand_read_on_it_is_said_to_be_named_so(
        string culture, string named, string says, string none)
    {
        Culture(culture);

        // Nutrinor's station on an element named "Sonic", which Essence read as its brand.
        Assert.StartsWith($"{named} ", OsmEditTexts.BrandNote(_l, Tags(("name", "Sonic")), osmBrand: "Sonic"));
        Assert.StartsWith($"{says} ", OsmEditTexts.BrandNote(_l, Tags(("name", "Sonic"), ("brand", "Ultramar")), osmBrand: "Sonic"));
        // A brand read from its operator: its name is its own, and it has no brand tag.
        Assert.StartsWith($"{none} ", OsmEditTexts.BrandNote(_l, Tags(("name", "Super Sagamie"), ("operator", "Sonic")), osmBrand: "Sonic"));
    }

    [Theory]
    [InlineData("en-CA", "Update opening hours of Costco", "Update opening hours and brand of Nutrinor Énergies")]
    [InlineData("fr-CA", "Mise à jour des heures d’ouverture de Costco", "Mise à jour des heures d’ouverture et de la marque de Nutrinor Énergies")]
    public void The_changeset_s_comment_says_the_brand_only_when_it_s_set_too(string culture, string hours, string hoursAndBrand)
    {
        Culture(culture);

        Assert.Equal(hours, OsmEditTexts.Comment(_l, "Costco", brand: null));
        Assert.Equal(hoursAndBrand, OsmEditTexts.Comment(_l, "Nutrinor Énergies", brand: "Nutrinor Énergies"));
    }

    private static OsmElementTags Tags(params (string Key, string Value)[] tags) =>
        new(tags.Append((Key: "amenity", Value: "fuel")).ToDictionary(t => t.Key, t => t.Value));

    private static void Culture(string name) =>
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
}
