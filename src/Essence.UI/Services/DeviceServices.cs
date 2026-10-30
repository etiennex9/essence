using Microsoft.JSInterop;

namespace Essence.UI.Services;

public enum LocationStatus
{
    Ok,
    Denied,
    Unavailable,
}

public sealed record DeviceLocation(LocationStatus Status, double Latitude = 0, double Longitude = 0, double AccuracyMeters = 0)
{
    public GeoPoint Point => new(Latitude, Longitude);
}

/// <summary>Where the device is. The browser implementation uses <c>navigator.geolocation</c>; a MAUI host registers a native one.</summary>
public interface IDeviceLocation
{
    Task<DeviceLocation> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>Small per-device settings, such as the selected fuel.</summary>
public interface IPreferenceStore
{
    ValueTask<string?> GetAsync(string key);

    ValueTask SetAsync(string key, string value);
}

/// <summary>
/// What lasts as long as the tab: through a reload, and when the browser discards the tab in the background and restores
/// it (Android does while Google Maps navigates), but not into another tab.
/// </summary>
public interface ISessionStore
{
    ValueTask<string?> GetAsync(string key);

    ValueTask SetAsync(string key, string value);

    ValueTask RemoveAsync(string key);
}

/// <summary>Geolocation, localStorage and sessionStorage through <c>essenceDevice.js</c>.</summary>
public sealed class BrowserDevice(IJSRuntime js) : IDeviceLocation, IPreferenceStore, IAsyncDisposable
{
    private const string ModulePath = "./_content/Essence.UI/js/essenceDevice.js";
    private const int TimeoutMilliseconds = 10_000;

    private Task<IJSObjectReference>? _module;

    public async Task<DeviceLocation> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync();
        var position = await module.InvokeAsync<JsPosition>("getPosition", cancellationToken, TimeoutMilliseconds);
        return position.Status switch
        {
            "ok" => new DeviceLocation(LocationStatus.Ok, position.Latitude, position.Longitude, position.AccuracyMeters),
            "denied" => new DeviceLocation(LocationStatus.Denied),
            _ => new DeviceLocation(LocationStatus.Unavailable),
        };
    }

    public async ValueTask<string?> GetAsync(string key) => await (await GetModuleAsync()).InvokeAsync<string?>("getItem", key);

    public async ValueTask SetAsync(string key, string value) => await (await GetModuleAsync()).InvokeVoidAsync("setItem", key, value);

    /// <summary>The tab's own storage (<see cref="ISessionStore"/>), through <see cref="Session"/>.</summary>
    public ISessionStore Session => new BrowserSession(this);

    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await (await _module).DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The page is going away; nothing to release.
        }
    }

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private sealed record JsPosition(string Status, double Latitude, double Longitude, double AccuracyMeters);

    private sealed class BrowserSession(BrowserDevice device) : ISessionStore
    {
        public async ValueTask<string?> GetAsync(string key) =>
            await (await device.GetModuleAsync()).InvokeAsync<string?>("getSessionItem", key);

        public async ValueTask SetAsync(string key, string value) =>
            await (await device.GetModuleAsync()).InvokeVoidAsync("setSessionItem", key, value);

        public async ValueTask RemoveAsync(string key) =>
            await (await device.GetModuleAsync()).InvokeVoidAsync("removeSessionItem", key);
    }
}
