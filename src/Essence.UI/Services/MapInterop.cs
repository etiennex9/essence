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

/// <summary>What the page keeps across a reload for a new release, besides the map's view.</summary>
/// <param name="StationPushed">The page pushed the <c>?station=</c> history entry (MapPage), so closing still goes back.</param>
/// <param name="ListPushed">The same for <c>?view=</c>.</param>
/// <param name="CarsPushed">The same for <c>?view=cars</c>, pushed over Best near you or a trip.</param>
/// <param name="TripPushed">The same for <c>?view=trip</c>, pushed over Best near you.</param>
/// <param name="CarsOverTrip">My cars is open over a trip, which the map keeps showing.</param>
/// <param name="PlacesPushed">The same as <paramref name="CarsPushed"/> for <c>?view=places</c>, pushed over Best near you.</param>
/// <param name="BestSelectedId">The station Best near you shows on top (<see cref="BestNearYou.SelectedId"/>); null for the pick.</param>
public sealed record ReloadState(
    string Fuel, bool StationPushed, bool ListPushed, bool CarsPushed, bool TripPushed, bool CarsOverTrip, bool PlacesPushed,
    long? BestSelectedId);

/// <summary>The view and state a reload for a new release left for the page it reloaded into.</summary>
/// <param name="UserLat">Where the user's dot was, if it was shown.</param>
public sealed record SavedView(
    double Lat,
    double Lon,
    double Zoom,
    double? UserLat,
    double? UserLon,
    string? Fuel,
    bool StationPushed,
    bool ListPushed,
    bool CarsPushed,
    bool TripPushed,
    bool CarsOverTrip,
    bool PlacesPushed,
    long? BestSelectedId);

/// <summary>What the map reports back.</summary>
public interface IMapListener
{
    Task OnViewportChangedAsync(Viewport viewport);

    Task OnMarkerClickedAsync(long id);

    /// <summary>Escape.</summary>
    Task OnDismissAsync();

    /// <summary>A click on the map itself, not on a marker.</summary>
    Task OnMapClickedAsync();

    /// <summary>The page was hidden (another tab, the app in the background) or shown again.</summary>
    Task OnVisibilityChangedAsync(bool visible);

    /// <summary>A press and hold on the map, not on a marker (a right-click on desktops).</summary>
    Task OnMapHeldAsync(double latitude, double longitude);
}

/// <summary>The MapLibre map behind <c>essenceMap.js</c>: the JS side owns the map and the marker DOM, C# everything else.</summary>
public sealed class MapInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/Essence.UI/js/essenceMap.js";

    private Task<IJSObjectReference>? _module;
    private IJSObjectReference? _map;
    private DotNetObjectReference<MapInterop>? _self;
    private IMapListener? _listener;

    public async Task CreateAsync(ElementReference container, MapOptions options, IMapListener listener)
    {
        _listener = listener;
        _self = DotNetObjectReference.Create(this);
        _map = await (await GetModuleAsync()).InvokeAsync<IJSObjectReference>("createMap", container, options, _self);
    }

    /// <summary>The view a reload for a new release saved for this page, if this page is that reload.</summary>
    public async Task<SavedView?> TakeSavedViewAsync() => await (await GetModuleAsync()).InvokeAsync<SavedView?>("takeSavedView");

    /// <summary>
    /// Reloads the page for a new release. With <paramref name="saveView"/>, it reopens on this view and
    /// <paramref name="state"/> (<see cref="TakeSavedViewAsync"/>); an <paramref name="automatic"/> reload that can't save
    /// them doesn't happen.
    /// </summary>
    public ValueTask ReloadAsync(ReloadState state, bool saveView, bool automatic) => InvokeAsync("reload", state, saveView, automatic);

    public ValueTask SetMarkersAsync(IReadOnlyList<MarkerModel> markers) => InvokeAsync("setMarkers", markers);

    public ValueTask SetSelectedAsync(long? id) => InvokeAsync("setSelected", id);

    /// <summary>
    /// Best near you on the map: the pick and the closest stand out, flagged with <paramref name="bestFlag"/> and
    /// <paramref name="closestFlag"/>, and with <paramref name="listed"/>, the stations not in it fade. Nulls clear it.
    /// </summary>
    public ValueTask SetEmphasisAsync(
        long? pickId, long? closestId, IReadOnlyCollection<long>? listed, string bestFlag, string closestFlag) =>
        InvokeAsync("setEmphasis", pickId, closestId, listed, bestFlag, closestFlag);

    public ValueTask ShowUserLocationAsync(GeoPoint point) => InvokeAsync("showUserLocation", point.Latitude, point.Longitude);

    /// <summary>
    /// A trip's route, [longitude, latitude] pairs: the part the car reaches, then the part past its range, dashed
    /// (<paramref name="beyond"/>, null when it reaches the end), with the corridor stations were looked for in
    /// (<see cref="TripCorridor.Polygons"/>, GeoJSON MultiPolygon coordinates).
    /// </summary>
    public ValueTask SetRouteAsync(double[][] path, double[][]? beyond, double[][][][] corridor) =>
        InvokeAsync("setRoute", path, beyond, corridor);

    public ValueTask ClearRouteAsync() => InvokeAsync("clearRoute");

    /// <summary>The trip's destination, labelled; null removes it.</summary>
    public ValueTask SetDestinationAsync(GeoPoint? point, string label) =>
        InvokeAsync("setDestination", point?.Latitude, point?.Longitude, label);

    /// <summary>Where the range runs out along the route, labelled; null removes it.</summary>
    public ValueTask SetRangeEndAsync(GeoPoint? point, string label) =>
        InvokeAsync("setRangeEnd", point?.Latitude, point?.Longitude, label);

    /// <summary>The pin a press and hold dropped; null removes it.</summary>
    public ValueTask SetPinAsync(GeoPoint? point) => InvokeAsync("setPin", point?.Latitude, point?.Longitude);

    /// <summary>Ends with the point in the middle of the part of the map the panes leave uncovered.</summary>
    /// <param name="waitForPane">Waits for a pane opening to be in place first.</param>
    public ValueTask FlyToAsync(GeoPoint point, double zoom, bool waitForPane = false) =>
        InvokeAsync("flyTo", point.Latitude, point.Longitude, zoom, waitForPane);

    /// <summary>Shows a box in the part of the map the panes leave uncovered.</summary>
    /// <param name="waitForPane">Waits for a pane opening to be in place first.</param>
    /// <param name="flags">The latitudes in the box where a station's flag stands above its point: it's kept clear of the toolbar.</param>
    /// <param name="labels">The same for a label, which is shorter.</param>
    public ValueTask FitBoundsAsync(
        double west, double south, double east, double north, bool waitForPane, IReadOnlyList<double> flags, IReadOnlyList<double> labels) =>
        InvokeAsync("fitBounds", west, south, east, north, waitForPane, flags, labels);

    /// <summary>Pans just enough to bring a point out from under the details pane.</summary>
    /// <param name="waitForPane">Waits for a pane opening to be in place first; a pane already open needn't be waited for.</param>
    public ValueTask EnsureVisibleAsync(GeoPoint point, bool waitForPane = true) =>
        InvokeAsync("ensureVisible", point.Latitude, point.Longitude, waitForPane);

    /// <summary>
    /// A click on the map, on phones: the sheet open shrinks to its header, and true says the click was the sheet's (one
    /// already shrunk stays). False on wide screens or with no sheet open.
    /// </summary>
    public async Task<bool> CollapsePaneAsync() => _map is not null && await _map.InvokeAsync<bool>("collapsePane");

    /// <summary>
    /// The page shows something else (another station, list or trip): the map's last automatic move isn't made again when a
    /// pane changes size, as it is until the map is moved by hand.
    /// </summary>
    public ValueTask ForgetAutoMoveAsync() => InvokeAsync("forgetAutoMove");

    [JSInvokable]
    public Task OnViewportChanged(Viewport viewport) => _listener?.OnViewportChangedAsync(viewport) ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnMarkerClicked(long id) => _listener?.OnMarkerClickedAsync(id) ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnDismiss() => _listener?.OnDismissAsync() ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnMapClicked() => _listener?.OnMapClickedAsync() ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnVisibilityChanged(bool visible) => _listener?.OnVisibilityChangedAsync(visible) ?? Task.CompletedTask;

    [JSInvokable]
    public Task OnMapHeld(double latitude, double longitude) => _listener?.OnMapHeldAsync(latitude, longitude) ?? Task.CompletedTask;

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
                await (await _module).DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is going away; the map goes with it.
        }

        _self?.Dispose();
    }

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private ValueTask InvokeAsync(string method, params object?[] args) =>
        _map is null ? ValueTask.CompletedTask : _map.InvokeVoidAsync(method, args);
}
