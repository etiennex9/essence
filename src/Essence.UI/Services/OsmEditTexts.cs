using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.UI.Services;

/// <summary>
/// What the hours editor (HoursEditor.razor) says of the OpenStreetMap element a correction goes to, in the user's
/// language. Pure, for the tests.
/// </summary>
public static class OsmEditTexts
{
    /// <summary>
    /// The element as "OpenStreetMap: Dépanneur Sonic" names it: its name, else its brand, else "unnamed station". Its name
    /// is OpenStreetMap's, often years old: it says which element the hours go to, not what the station is called.
    /// </summary>
    public static string ElementName(IStringLocalizer<Strings> l, OsmElementTags tags) => tags.Label ?? l["OsmUnnamed"];

    /// <summary>
    /// The note under "Also set its brand to …": what OpenStreetMap says now, when the element could be read ("OpenStreetMap
    /// says Sonic.", its <c>brand</c>; "OpenStreetMap names it Sonic.", with no <c>brand</c> but named after the brand
    /// Essence read on it, a name that changes with the brand; or that it has no brand for it), then that it's only for
    /// what the user knows first-hand, OpenStreetMap not taking Google Maps or Street View as a source.
    /// </summary>
    /// <param name="osmBrand">The element's brand as Essence last read it (<see cref="StationDetail.OsmBrand"/>); null for none, or unknown.</param>
    public static string BrandNote(IStringLocalizer<Strings> l, OsmElementTags? now, string? osmBrand = null) =>
        now is null
            ? l["OsmBrandFirstHand"]
            : $"{Now(l, now, osmBrand)} {l["OsmBrandFirstHand"]}";

    private static string Now(IStringLocalizer<Strings> l, OsmElementTags now, string? osmBrand) =>
        now.Brand is { } brand ? l["OsmBrandNow", brand]
        : now.IsNamedAfterBrand(osmBrand) ? l["OsmBrandNamed", now.Name!]
        : l["OsmNoBrandNow"];

    /// <summary>
    /// The changeset's comment: "Update opening hours of Costco", or "Update opening hours and brand of Nutrinor Énergies"
    /// when the station's brand is set too (<paramref name="brand"/>).
    /// </summary>
    /// <param name="name">The station's name as its details title it: its brand, or "Independent".</param>
    public static string Comment(IStringLocalizer<Strings> l, string name, string? brand) =>
        brand is null ? l["OsmChangesetComment", name] : l["OsmChangesetCommentBrand", name];
}
