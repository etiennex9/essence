using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.Core.Location;
using Essence.Mobile.Services;
using Essence.UI.App;
using Essence.UI.App.Widgets;
using Essence.UI.Services;
using AndroidLocation = Android.Locations.Location;
using Manifest = Android.Manifest;

#if ESSENCE_BACKGROUND_LOCATION
// Only in builds that may follow where you are (Essence.Mobile.csproj's EssenceBackgroundLocation); asked for in
// WidgetLocationActivity alone, after saying what it's for.
[assembly: UsesPermission(Manifest.Permission.AccessBackgroundLocation)]
#endif

namespace Essence.Mobile.Widgets;

/// <summary>
/// The device's location for the widgets, from code that runs without the app on screen: ↻ (<see cref="WidgetLocationService"/>),
/// a refresh, a move (<see cref="WidgetLocationReceiver"/>). Never MAUI's Geolocation or Permissions: they ask for the
/// permission when it's missing, which needs an activity, and take Approximate alone for a refusal. Coordinates are never
/// logged.
/// </summary>
internal static class WidgetLocator
{
    /// <summary>Manifest.Permission.AccessBackgroundLocation, which the analyzer holds to Android 10's checks.</summary>
    public const string BackgroundPermission = "android.permission.ACCESS_BACKGROUND_LOCATION";

    /// <summary>A fix Android kept this recently is used as is.</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromSeconds(30);

    public static bool HasLocation(Context context) => Granted(context, Manifest.Permission.AccessCoarseLocation);

    public static bool HasPrecise(Context context) => Granted(context, Manifest.Permission.AccessFineLocation);

    /// <summary>"Allow all the time"; before Android 10, the location permission was all there was to allow.</summary>
    public static bool HasBackground(Context context) =>
        OperatingSystem.IsAndroidVersionAtLeast(29) ? Granted(context, BackgroundPermission) : HasLocation(context);

    /// <summary>The user turned on Follow (<see cref="WidgetLocation.FollowKey"/>), read where the widgets read it.</summary>
    public static bool IsFollowOn() => Preferences.Default.Get<string?>(WidgetLocation.FollowKey, null) == "true";

    /// <summary>The device's location setting (Quick settings → Location).</summary>
    public static bool IsLocationOn(Context context) =>
        context.GetSystemService(Context.LocationService) is LocationManager manager && LocationManagerCompat.IsLocationEnabled(manager);

    public static WidgetLocationAccess Access(Context context) =>
        WidgetLocation.AccessOf(HasLocation(context), HasBackground(context), IsFollowOn(), BuildInfo.Current.BackgroundLocation);

    /// <summary>What About shows; null in a build that can't follow.</summary>
    public static WidgetFollowState? FollowState(Context context) =>
        BuildInfo.Current.BackgroundLocation ? WidgetLocation.FollowStateOf(IsFollowOn(), Access(context)) : null;

    /// <summary>
    /// Where the device is: a fix Android took in the last <see cref="Recent"/>, else a new one within <paramref name="timeout"/>,
    /// else the newest Android kept (its own time says how old). Null without the permission, with the location off, or with
    /// nothing at all. GPS only when <paramref name="allowGps"/> (a tap, never a refresh in the background).
    /// </summary>
    public static async Task<LastFix?> TryFixAsync(Context context, TimeSpan timeout, bool allowGps, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!HasLocation(context) || context.GetSystemService(Context.LocationService) is not LocationManager manager
            || !LocationManagerCompat.IsLocationEnabled(manager))
        {
            return null;
        }

        try
        {
            var kept = Newest(manager);
            if (kept is not null && AgeOf(kept) < Recent)
            {
                return FixOf(kept, time);
            }

            var fresh = ProviderFor(context, manager, allowGps) is { } provider
                ? await CurrentAsync(context, manager, provider, timeout, cancellationToken)
                : null;
            return (fresh ?? kept) is { } location ? FixOf(location, time) : null;
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            // The permission went, or the provider did, while asking.
            return null;
        }
    }

    /// <summary>A fix with the time the device took it, from how long ago that was: a changed clock doesn't move it.</summary>
    public static LastFix FixOf(AndroidLocation location, TimeProvider time) => new(
        location.Latitude,
        location.Longitude,
        location.HasAccuracy ? location.Accuracy : 0,
        time.GetUtcNow() - AgeOf(location));

    private static TimeSpan AgeOf(AndroidLocation location) =>
        TimeSpan.FromTicks(Math.Max(0, SystemClock.ElapsedRealtimeNanos() - location.ElapsedRealtimeNanos) / 100);

    private static AndroidLocation? Newest(LocationManager manager)
    {
        AndroidLocation? newest = null;
        foreach (var provider in manager.GetProviders(enabledOnly: true) ?? [])
        {
            try
            {
                if (manager.GetLastKnownLocation(provider) is { } location
                    && (newest is null || location.ElapsedRealtimeNanos > newest.ElapsedRealtimeNanos))
                {
                    newest = location;
                }
            }
            catch (Java.Lang.SecurityException)
            {
                // GPS with Approximate only.
            }
        }

        return newest;
    }

    /// <summary>Android's fused provider (API 31), else the network's, else GPS when allowed and precise.</summary>
    private static string? ProviderFor(Context context, LocationManager manager, bool allowGps)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && manager.IsProviderEnabled(LocationManager.FusedProvider))
        {
            return LocationManager.FusedProvider;
        }

        if (manager.IsProviderEnabled(LocationManager.NetworkProvider))
        {
            return LocationManager.NetworkProvider;
        }

        return allowGps && HasPrecise(context) && manager.IsProviderEnabled(LocationManager.GpsProvider) ? LocationManager.GpsProvider : null;
    }

    private static async Task<AndroidLocation?> CurrentAsync(
        Context context, LocationManager manager, string provider, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<AndroidLocation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = new Android.OS.CancellationSignal();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        using var registration = stop.Token.Register(() =>
        {
            signal.Cancel();
            answer.TrySetResult(null);
        });
        LocationManagerCompat.GetCurrentLocation(manager, provider, signal, ContextCompat.GetMainExecutor(context), new Consumer(answer));
        return await answer.Task;
    }

    private static bool Granted(Context context, string permission) =>
        ContextCompat.CheckSelfPermission(context, permission) == Permission.Granted;

    private sealed class Consumer(TaskCompletionSource<AndroidLocation?> answer) : Java.Lang.Object, AndroidX.Core.Util.IConsumer
    {
        public void Accept(Java.Lang.Object? value) => answer.TrySetResult(value as AndroidLocation);
    }
}

/// <summary>
/// ↻ on a widget that ranks from where you are: one fix, then a refresh. A foreground service started by a tap on a widget
/// may use the location with only "While using the app" (Android's exemptions for widgets), which a broadcast or WorkManager
/// may not, so ↻ starts it directly (<see cref="WidgetViews"/>). From Android 12 its notice rarely shows: Android waits 10 s
/// before showing one, and a fix takes less. Its Java name is fixed, as the widgets' are.
/// </summary>
[Service(
    Name = "io.github.etiennex9.essence.widgets.WidgetLocationService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeLocation | ForegroundService.TypeShortService)]
public sealed class WidgetLocationService : Service
{
    private const string Channel = "essence.widget-location";
    private const int NoticeId = 7301;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private int _locating;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // First: started as a foreground service, it has a few seconds to become one, or Android stops the app.
        StartForegroundNow();
        if (Interlocked.Exchange(ref _locating, 1) == 0)
        {
            _ = LocateAsync();
        }

        return StartCommandResult.NotSticky;
    }

    private void StartForegroundNow()
    {
        var notice = Notice();
        try
        {
            // Types are Android 10's; before, the manifest's type stands.
            ServiceCompat.StartForeground(this, NoticeId, notice, OperatingSystem.IsAndroidVersionAtLeast(29) ? (int)ForegroundService.TypeLocation : 0);
        }
        catch (Java.Lang.SecurityException)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                throw;
            }

            // The location permission went since the widget was drawn (Android 14+ checks it here): a short service then, so
            // the app isn't stopped, and the refresh shows the widget without ↻ locating.
            ServiceCompat.StartForeground(this, NoticeId, notice, (int)ForegroundService.TypeShortService);
        }
    }

    private async Task LocateAsync()
    {
        try
        {
            var services = IPlatformApplication.Current!.Services;
            var time = services.GetRequiredService<TimeProvider>();
            if (await WidgetLocator.TryFixAsync(this, Timeout, allowGps: true, time, CancellationToken.None) is { } fix)
            {
                await LastLocation.WriteIfNewerAsync(services.GetRequiredService<IPreferenceStore>(), fix);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No fix: the refresh shows the last one, with its time.
        }
        finally
        {
            WidgetUpdates.RefreshNow(this);
            ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
            StopSelf();
            Volatile.Write(ref _locating, 0);
        }
    }

    private Notification Notice()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && GetSystemService(NotificationService) is NotificationManager notifications)
        {
            notifications.CreateNotificationChannel(
                new NotificationChannel(Channel, GetString(Resource.String.widget_location_channel), NotificationImportance.Low));
        }

        return new NotificationCompat.Builder(this, Channel)
            .SetSmallIcon(Resource.Drawable.notification_location)!
            .SetContentTitle(GetString(Resource.String.widget_location_finding))!
            .SetOngoing(true)!
            .SetSilent(true)!
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceDeferred)!
            .Build()!;
    }
}

/// <summary>
/// Follow's movement trigger: Android's passive location (fixes other apps and the system already take, at no cost) at least
/// <see cref="WidgetLocation.MinMoveInterval"/> and <see cref="WidgetLocation.MinMoveMeters"/> apart, sent to
/// <see cref="WidgetLocationReceiver"/>. Android drops it at a reboot or a force stop; every refresh asks for it again, and the
/// widgets' update at boot runs a refresh.
/// </summary>
internal static class WidgetTracking
{
    public static void Update(Context context, bool wanted)
    {
        if (context.GetSystemService(Context.LocationService) is not LocationManager manager)
        {
            return;
        }

        var updates = Updates(context);
        try
        {
            if (wanted)
            {
                // The same PendingIntent replaces the request rather than adding one.
                manager.RequestLocationUpdates(
                    LocationManager.PassiveProvider, (long)WidgetLocation.MinMoveInterval.TotalMilliseconds, (float)WidgetLocation.MinMoveMeters, updates);
            }
            else
            {
                manager.RemoveUpdates(updates);
            }
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            // No permission or no passive provider: the periodic refreshes still follow.
        }
    }

    /// <summary>
    /// Mutable, the one exception to the app's immutable PendingIntents: Android adds the location to it (and before Android 12,
    /// a PendingIntent is mutable unless said otherwise).
    /// </summary>
    private static PendingIntent Updates(Context context) => PendingIntent.GetBroadcast(
        context,
        0,
        new Intent(context, typeof(WidgetLocationReceiver)),
        OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent : PendingIntentFlags.UpdateCurrent)!;
}

/// <summary>
/// A passive fix (<see cref="WidgetTracking"/>): kept, and when it moved from where the widgets ranked, a refresh. Nothing else
/// here: Samsung freezes the app in the background, so the work goes through WorkManager. Not exported; its Java name is fixed,
/// since Android holds the request by it.
/// </summary>
[BroadcastReceiver(Name = "io.github.etiennex9.essence.widgets.WidgetLocationReceiver", Exported = false)]
public sealed class WidgetLocationReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || LocationOf(intent) is not { } location)
        {
            return;
        }

        if (WidgetLocator.Access(context) != WidgetLocationAccess.AllTheTime)
        {
            WidgetTracking.Update(context, wanted: false);
            return;
        }

        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var services = IPlatformApplication.Current!.Services;
                var preferences = services.GetRequiredService<IPreferenceStore>();
                var fix = WidgetLocator.FixOf(location, services.GetRequiredService<TimeProvider>());
                if (await LastLocation.WriteIfNewerAsync(preferences, fix)
                    && WidgetLocation.WorthRefreshing(await WidgetLocation.ReadRankedFromAsync(preferences), fix))
                {
                    WidgetUpdates.RefreshNow(context);
                }
            }
            finally
            {
                pending?.Finish();
            }
        });
    }

    private static AndroidLocation? LocationOf(Intent? intent) =>
        intent is null ? null
        : OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableExtra(LocationManager.KeyLocationChanged, Java.Lang.Class.FromType(typeof(AndroidLocation))) as AndroidLocation
#pragma warning disable CA1422 // Before Android 13, the untyped one is all there is.
            : intent.GetParcelableExtra(LocationManager.KeyLocationChanged) as AndroidLocation;
#pragma warning restore CA1422
}
