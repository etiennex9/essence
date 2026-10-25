using System.Globalization;
using System.Text;

namespace Essence.Ranking;

/// <summary>Québec, where trips and searched places must be.</summary>
public static class QuebecArea
{
    // Québec's bounding box, a little wider. Ottawa, Edmundston and Vermont are inside it: Contains tells those apart.
    public const double South = 44.9;
    public const double North = 62.7;
    public const double West = -79.9;
    public const double East = -57;

    private static readonly Lazy<Ring[]> Rings = new(LoadRings);

    public static bool InBox(LatLon point) =>
        point.Latitude is >= South and <= North && point.Longitude is >= West and <= East;

    /// <summary>
    /// Whether a point is in Québec, by the province's boundary from OpenStreetMap (QuebecBoundary.txt, within 20 m along
    /// the borders south of 49.5° N, 300 m north of it). The Gulf of St. Lawrence is in it, as the province's own boundary
    /// has it; a point in the water is refused for having no road.
    /// </summary>
    public static bool Contains(LatLon point)
    {
        if (!InBox(point))
        {
            return false;
        }

        // Even–odd: inside when a ray from the point crosses the rings an odd number of times (a hole's crossings undo an
        // outer ring's).
        var inside = false;
        foreach (var ring in Rings.Value)
        {
            if (point.Latitude < ring.South || point.Latitude > ring.North || point.Longitude < ring.West || point.Longitude > ring.East)
            {
                continue;
            }

            var points = ring.Points;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var (a, b) = (points[i], points[j]);
                if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude)
                    && point.Longitude < a.Longitude + (point.Latitude - a.Latitude) / (b.Latitude - a.Latitude) * (b.Longitude - a.Longitude))
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>Photon's province is Québec's, in either language ("Québec", "Quebec"), whatever the case or accents.</summary>
    public static bool IsQuebec(string? state) => state is not null && Places.Fold(state) == "quebec";

    private static Ring[] LoadRings()
    {
        using var stream = typeof(QuebecArea).Assembly.GetManifestResourceStream("Essence.Ranking.QuebecBoundary.txt")
            ?? throw new InvalidOperationException("QuebecBoundary.txt isn't embedded in Essence.Ranking.");
        using var reader = new StreamReader(stream);
        var rings = new List<Ring>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var points = Polyline.Decode(line.TrimStart('!'), precision: 5);
            rings.Add(new Ring(
                points, points.Min(p => p.Latitude), points.Max(p => p.Latitude), points.Min(p => p.Longitude), points.Max(p => p.Longitude)));
        }

        return [.. rings];
    }

    private sealed record Ring(List<LatLon> Points, double South, double North, double West, double East);
}

/// <summary>What a place is, for its icon: an address, a street, a town or part of one, or a named place.</summary>
public static class PlaceKinds
{
    public const string Address = "address";
    public const string Street = "street";
    public const string Town = "town";
    public const string Named = "named";
}

/// <param name="Label">What the place is called: "1000 Rue De La Gauchetière Ouest", "Rue Gaucher", "Laval", "Cégep de Sherbrooke".</param>
/// <param name="Detail">Where it is: "Montréal, QC H3A 4W5" for an address, the town for a street, the address for a named place.</param>
/// <param name="Kind">One of <see cref="PlaceKinds"/>.</param>
public sealed record PlaceLabel(string Label, string Detail, string Kind);

/// <summary>How Photon's results become suggestions: Québec's only, each place once, labelled as a person would say it.</summary>
public static class Places
{
    /// <summary>At most this many suggestions.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>Photon is asked for this many, since some are dropped (outside Québec, or the same place twice).</summary>
    public const int SearchLimit = 20;

    /// <summary>A pin's address is looked for within this.</summary>
    public const double ReverseRadiusKm = 1;

    /// <summary>A pin is labelled with an address this close, rather than a nearer street or named place.</summary>
    private const double AddressPreferredWithinKm = 0.1;

    /// <summary>The same name this close is the same place (an address entered twice, with two postal codes).</summary>
    private const double SamePlaceWithinKm = 0.15;

    /// <summary>
    /// The suggestions for what was typed: in Québec, each place once, at most <see cref="MaxSuggestions"/>, in Photon's
    /// order, except that a named place found by its address (a business at the "1000" of "1000 de la Gauchetière", whose
    /// name doesn't hold that number) comes after the others: the address itself first.
    /// </summary>
    public static IReadOnlyList<(PhotonPlace Place, PlaceLabel Label)> Suggestions(IEnumerable<PhotonPlace> places, string typed)
    {
        var words = Words(typed);
        var ordered = places
            .Select(p => (Place: p, Label: QuebecArea.IsQuebec(p.State) ? Describe(p) : null))
            .Where(x => x.Label is not null)
            .OrderBy(x => FoundByItsAddress(x.Place, x.Label!, words));
        var kept = new List<(PhotonPlace Place, PlaceLabel Label)>();
        foreach (var (place, label) in ordered.Select(x => (x.Place, x.Label!)))
        {
            var folded = Fold(label.Label);
            // A long street comes as one feature per stretch: the same name in the same town is one suggestion, the
            // nearest (Photon's first). An address entered twice may differ by its postal code: the same name close by.
            if (kept.Exists(k => Fold(k.Label.Label) == folded
                && (Fold(k.Label.Detail) == Fold(label.Detail)
                    || Geo.DistanceKm(k.Place.Location.Latitude, k.Place.Location.Longitude, place.Location.Latitude, place.Location.Longitude)
                        < SamePlaceWithinKm)))
            {
                continue;
            }

            kept.Add((place, label));
            if (kept.Count == MaxSuggestions)
            {
                break;
            }
        }

        return kept;
    }

    /// <summary>
    /// What to call a point, from the places nearest it (<see cref="PhotonClient.ReverseAsync"/>, nearest first): the
    /// nearest address within 100 m, else the nearest street or named place. An address farther away names the point by
    /// its street only (a pin on a range road is on that road, not at the house 400 m down it). Null when there's none.
    /// </summary>
    public static (PhotonPlace Place, PlaceLabel Label)? Nearest(LatLon point, IReadOnlyList<PhotonPlace> nearest)
    {
        var described = nearest
            .Select(p => (Place: p, Label: Describe(p)))
            .Select(x => x.Label?.Kind == PlaceKinds.Address
                && Geo.DistanceKm(point.Latitude, point.Longitude, x.Place.Location.Latitude, x.Place.Location.Longitude) > AddressPreferredWithinKm
                    ? (x.Place, Label: Labelled(x.Place.Street, Town(x.Place), PlaceKinds.Street))
                    : x)
            .Where(x => x.Label is not null)
            .ToList();
        if (described.Count == 0)
        {
            return null;
        }

        var (place, label) = described.Find(x => x.Label!.Kind == PlaceKinds.Address) is { Place: not null } address ? address : described[0];
        return (place, label!);
    }

    /// <summary>The place's label, or null when it has nothing to be called by.</summary>
    public static PlaceLabel? Describe(PhotonPlace place)
    {
        var town = Town(place);
        var street = place.HouseNumber is { } number && place.Street is { } name ? $"{number} {name}" : place.Street;
        switch (place.Type)
        {
            case "street":
                return Labelled(place.Name ?? place.Street, town, PlaceKinds.Street);

            case "city":
                return Labelled(place.Name, place.County, PlaceKinds.Town);

            case "district" or "locality":
                return Labelled(place.Name, place.City ?? place.County, PlaceKinds.Town);
        }

        // A house, or another feature: an address, or a named place at one.
        if (place.Name is { } placeName && !IsNumberOnly(placeName, place.HouseNumber))
        {
            return Labelled(placeName, Join(street, town == placeName ? null : town), PlaceKinds.Named);
        }

        if (place.HouseNumber is not null && place.Street is not null)
        {
            var postal = place.Postcode is { } code ? $"QC {code}" : null;
            return Labelled(street, Join(town, postal), PlaceKinds.Address);
        }

        return Labelled(street ?? place.Name, town, PlaceKinds.Address);
    }

    /// <summary>Lower case, without accents or extra spaces: "Québec" and "QUEBEC" are the same.</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Town(PhotonPlace place) => place.City ?? place.Locality ?? place.District ?? place.County;

    /// <summary>A named place at the house number typed, whose name doesn't hold that number: found by its address.</summary>
    private static bool FoundByItsAddress(PhotonPlace place, PlaceLabel label, string[] typed) =>
        label.Kind == PlaceKinds.Named
        && place.HouseNumber is { } number
        && typed.Contains(Fold(number))
        && !Words(label.Label).Contains(Fold(number));

    private static string[] Words(string text) => Fold(text).Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

    private static readonly char[] WordSeparators = [' ', ',', '-', '\'', '’', '.', '(', ')'];

    private static PlaceLabel? Labelled(string? label, string? detail, string kind) =>
        string.IsNullOrWhiteSpace(label) ? null : new PlaceLabel(label, detail ?? "", kind);

    /// <summary>"Montréal, QC H3A 4W5": the parts there are.</summary>
    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => $"{a}, {b}",
        };

    /// <summary>A "name" that's only the house number (OSM sometimes tags a building with its number as its name).</summary>
    private static bool IsNumberOnly(string name, string? houseNumber) => name == houseNumber;
}
