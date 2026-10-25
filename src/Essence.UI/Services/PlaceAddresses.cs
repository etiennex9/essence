using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>What a point is called, from the API's reverse lookup.</summary>
/// <param name="Place">Its address, or the street or named place nearest it; null when there's none or the lookup failed.</param>
/// <param name="OutsideQuebec">The point is past Québec's border: trips don't go there.</param>
/// <param name="Error">Why there's no answer: <see cref="ApiError.NotFound"/> when nothing is near, else the lookup failed.</param>
public sealed record PlaceLookup(PlaceSuggestion? Place, bool OutsideQuebec, ApiError Error)
{
    /// <summary>The address search answered (with an address, nothing near, or outside Québec): worth keeping.</summary>
    public bool Answered => Place is not null || OutsideQuebec || Error == ApiError.NotFound;
}

/// <summary>
/// The addresses of dropped pins and saved places (<c>/places/reverse</c>), each point asked about once: answers are kept
/// for the session, failures aren't, so the next look asks again.
/// </summary>
public sealed class PlaceAddresses(EssenceApiClient api)
{
    private readonly Dictionary<(double, double, string), PlaceLookup> _answers = [];

    /// <param name="language"><c>fr</c> or <c>en</c>.</param>
    public async Task<PlaceLookup> LookupAsync(GeoPoint point, string language, CancellationToken cancellationToken)
    {
        // To about a metre: the same pin, or the same saved place.
        var key = (Math.Round(point.Latitude, 5), Math.Round(point.Longitude, 5), language);
        if (_answers.TryGetValue(key, out var known))
        {
            return known;
        }

        var result = await api.GetPlaceAsync(point, language, cancellationToken);
        var lookup = new PlaceLookup(result.Value, result.Error == ApiError.NoRoute, result.Error);
        if (lookup.Answered)
        {
            _answers[key] = lookup;
        }

        return lookup;
    }

    /// <summary>A short name for a place, to keep with a saved place or a trip: its label, and the town for an address or a street.</summary>
    public static string ShortName(PlaceSuggestion place)
    {
        if (place.Kind is not ("address" or "street") || place.Detail.Length == 0)
        {
            return place.Label;
        }

        // "Montréal, QC H3A 4W5" → "Montréal".
        var town = place.Detail.Split(", QC", 2)[0];
        return $"{place.Label}, {town}";
    }
}
