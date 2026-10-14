using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Essence.UI.Services;

/// <param name="Language">UI language ("en", "fr"), applied to the document so screen readers pronounce it right.</param>
public sealed record MapOptions(
    string StyleUrl, string DarkStyleUrl, double CenterLat, double CenterLon, double Zoom, double CompactBelowZoom, string Language);

/// <summary>One marker as <c>essenceMap.js</c> draws it. Everything is preformatted, so the JS side only builds DOM.</summary>
/// <param name="Tone">"low", "mid", "high" or "" (no tone).</param>
/// <param name="Z">Stacking order; higher draws on top.</param>
/// <param name="Favorite">Draws a star on the marker.</param>
public sealed record MarkerModel(
    long Id,
    double Lat,
    double Lon,
    string Label,
    string Price,
    bool Unavailable,
    string Tone,
    int Z,
    bool Favorite,
    string Monogram,
    string Background,
    string Foreground,
    string? LogoUrl);

/// <summary>What the map reports back.</summary>
public interface IMapListener
{
    Task OnViewportChangedAsync(Viewport viewport);

    Task OnMarkerClickedAsync(long id);

    /// <summary>A click on the map itself (not on a marker), or Escape.</summary>
    Task OnDismissAsync();
}

/// <summary>The MapLibre map behind <c>essenceMap.js</c>: the JS side owns the map and the marker DOM, C# everything else.</summary>
public sealed class MapInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/Essence.UI/js/essenceMap.js";

    private IJSObjectReference? _module;
    private IJSObjectReference? _map;
    private DotNetObjectReference<MapInterop>? _self;
    private IMapListener? _listener;

    public async Task CreateAsync(ElementReference container, MapOptions options, IMapListener listener)
    {
        _listener = listener;
        _self = DotNetObjectReference.Create(this);
        _module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        _map = await _module.InvokeAsync<IJSObjectReference>("createMap", container, options, _self);
    }

    public ValueTask SetMarkersAsync(IReadOnlyList<MarkerModel> markers) => InvokeAsync("setMarkers", markers);

    public ValueTask SetSelectedAsync(long? id) => InvokeAsync("setSelected", id);

    public ValueTask ShowUserLocationAsync(GeoPoint point) => InvokeAsync("showUserLocation", point.Latitude, point.Longitude);

    /// <param name="besidePane">Ends with the point in the middle of the area the details pane leaves uncovered.</param>
    public ValueTask FlyToAsync(GeoPoint point, double zoom, bool besidePane = false) =>
        InvokeAsync("flyTo", point.Latitude, point.Longitude, zoom, besidePane);

    /// <summary>Pans just enough to bring a point out from under the details pane.</summary>
    public ValueTask EnsureVisibleAsync(GeoPoint point) => InvokeAsync("ensureVisible", point.Latitude, point.Longitude);

    [JSInvokable]
    public Task OnViewportChanged(Viewport viewport) => _listener?.OnViewportChangedAsync(viewport) ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnMarkerClicked(long id) => _listener?.OnMarkerClickedAsync(id) ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnDismiss() => _listener?.OnDismissAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _listener = null;
        try
        {
            if (_map is not null)
            {
                await _map.InvokeVoidAsync("dispose");
                await _map.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is going away; the map goes with it.
        }

        _self?.Dispose();
    }

    private ValueTask InvokeAsync(string method, params object?[] args) =>
        _map is null ? ValueTask.CompletedTask : _map.InvokeVoidAsync(method, args);
}
