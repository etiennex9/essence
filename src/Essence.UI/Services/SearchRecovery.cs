using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>
/// Ties the address search to its index's status while the places panel is open: a search the API refused (503) asks
/// how far the index is, once per answer, not while the next search waits; and once the index goes from being prepared to
/// ready, what was typed is searched again (<paramref name="searchAgain"/>, on the panel's renderer). A 503 while the index
/// reads ready (Photon down) is shown as such, and asks nothing more.
/// </summary>
public sealed class SearchRecovery : IDisposable
{
    private readonly PlaceSearch _search;
    private readonly ServicesStatus _services;
    private readonly Action _searchAgain;

    public SearchRecovery(PlaceSearch search, ServicesStatus services, Action searchAgain)
    {
        (_search, _services, _searchAgain) = (search, services, searchAgain);
        _search.Changed += OnSearchChanged;
        _services.GeocodingReady += OnGeocodingReady;
    }

    private void OnSearchChanged()
    {
        if (_search.Error == ApiError.Unavailable && !_search.Loading)
        {
            _ = _services.RefreshAsync();
        }
    }

    private void OnGeocodingReady()
    {
        if (_search.Error == ApiError.Unavailable && _search.Searching)
        {
            _searchAgain();
        }
    }

    public void Dispose()
    {
        _search.Changed -= OnSearchChanged;
        _services.GeocodingReady -= OnGeocodingReady;
    }
}
