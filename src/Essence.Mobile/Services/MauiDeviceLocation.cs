using Essence.UI.App;
using Essence.UI.Services;

namespace Essence.Mobile.Services;

/// <summary>
/// The device's location from Android, asking for the permission while the app is in use. (The WebView's own geolocation
/// would wait for a permission answer the WebView never gives.) Each fix is also kept as <see cref="LastLocation"/>, with the
/// time the device took it, for the widgets that rank from where you are.
/// </summary>
public sealed class MauiDeviceLocation(MauiPreferenceStore preferences, TimeProvider time) : IDeviceLocation
{
    /// <summary>A fix this recent is used as is, as the web app's <c>maximumAge</c>.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<DeviceLocation> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Approximate location (Android 12+) is enough: MAUI calls it Restricted, or Denied before asking, since it wants the
            // precise one too. Asked only when not even that is allowed, so choosing Approximate isn't asked again each time.
            var allowed = HasApproximateLocation() || await MainThread.InvokeOnMainThreadAsync(async () =>
                await Permissions.RequestAsync<Permissions.LocationWhenInUse>() is PermissionStatus.Granted or PermissionStatus.Restricted);
            if (!allowed)
            {
                return new DeviceLocation(LocationStatus.Denied);
            }

            var location = await Geolocation.Default.GetLastKnownLocationAsync();
            if (location is null || time.GetUtcNow() - location.Timestamp > MaxAge)
            {
                location = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium, Timeout), cancellationToken);
            }

            if (location is null)
            {
                return new DeviceLocation(LocationStatus.Unavailable);
            }

            var accuracy = location.Accuracy ?? 0;
            // Never later than now: a fix stamped ahead (a GPS clock against a wrong phone clock) would hold off every next one.
            var at = location.Timestamp < time.GetUtcNow() ? location.Timestamp : time.GetUtcNow();
            await LastLocation.WriteIfNewerAsync(preferences, new LastFix(location.Latitude, location.Longitude, accuracy, at));
            return new DeviceLocation(LocationStatus.Ok, location.Latitude, location.Longitude, accuracy);
        }
        catch (PermissionException)
        {
            return new DeviceLocation(LocationStatus.Denied);
        }
        catch (Exception ex) when (ex is FeatureNotEnabledException or FeatureNotSupportedException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Location turned off, no provider, or no fix in time.
            return new DeviceLocation(LocationStatus.Unavailable);
        }
    }

    private static bool HasApproximateLocation() =>
        AndroidX.Core.Content.ContextCompat.CheckSelfPermission(Platform.AppContext, Android.Manifest.Permission.AccessCoarseLocation)
        == Android.Content.PM.Permission.Granted;
}
