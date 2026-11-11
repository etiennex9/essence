using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Essence.UI.App.Widgets;

namespace Essence.Mobile.Widgets;

/// <summary>The three home-screen widgets.</summary>
public enum WidgetKind
{
    /// <summary>The station Best near you picks from a place (<see cref="WidgetFeeds.BestNearAsync"/>).</summary>
    BestNear,

    /// <summary>The starred stations' prices (<see cref="WidgetFeeds.FavoritesAsync"/>).</summary>
    Favorites,

    /// <summary>Best on the way between two places (<see cref="WidgetFeeds.TripAsync"/>).</summary>
    Trip,
}

/// <summary>
/// What every widget does when Android asks: an update (added, after its settings, at boot) refreshes them all through
/// WorkManager, as does the ↻ button; a widget removed forgets its settings. Not exported: only Android's widget service and
/// the app itself send them anything. The names are fixed (BroadcastReceiver's Name):
/// Android keeps placed widgets by their class name, so renaming one would drop them from home screens.
/// </summary>
public abstract class EssenceWidget : AppWidgetProvider
{
    public const string RefreshAction = "io.github.etiennex9.essence.widgets.REFRESH";

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is not null)
        {
            WidgetUpdates.Schedule(context);
            WidgetUpdates.RefreshNow(context);
        }
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        base.OnReceive(context, intent);
        if (context is not null && intent?.Action == RefreshAction)
        {
            WidgetUpdates.RefreshNow(context);
        }
    }

    /// <summary>
    /// Resized: before Android 12, the stations that fit are worked out from the size, so the widget is built again. From
    /// Android 12 the launcher picks among the layouts it was given (WidgetViews), with nothing to do here.
    /// </summary>
    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        base.OnAppWidgetOptionsChanged(context, appWidgetManager, appWidgetId, newOptions);
        if (context is not null && !OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            WidgetUpdates.RefreshNow(context);
        }
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        foreach (var id in appWidgetIds ?? [])
        {
            Preferences.Default.Remove(WidgetConfigs.KeyFor(id));
            Preferences.Default.Remove(WidgetViewCache.KeyFor(id));
        }

        // The movement trigger stops with the last widget that followed (the refresh decides).
        if (context is not null)
        {
            WidgetUpdates.RefreshNow(context);
        }
    }

    /// <summary>Restored from a backup, widgets get new ids: their settings follow them.</summary>
    public override void OnRestored(Context? context, int[]? oldWidgetIds, int[]? newWidgetIds)
    {
        if (oldWidgetIds is null || newWidgetIds is null)
        {
            return;
        }

        foreach (var (oldId, newId) in oldWidgetIds.Zip(newWidgetIds))
        {
            if (Preferences.Default.Get<string?>(WidgetConfigs.KeyFor(oldId), null) is { } config)
            {
                Preferences.Default.Set(WidgetConfigs.KeyFor(newId), config);
                Preferences.Default.Remove(WidgetConfigs.KeyFor(oldId));
            }

            Preferences.Default.Remove(WidgetViewCache.KeyFor(oldId));
        }

        if (context is not null)
        {
            WidgetUpdates.RefreshNow(context);
        }
    }

    public override void OnDisabled(Context? context)
    {
        if (context is not null)
        {
            WidgetUpdates.StopIfUnused(context);
        }
    }

    /// <summary>The widget class of each kind, for Android's lists of placed widgets.</summary>
    public static ComponentName ComponentFor(Context context, WidgetKind kind) => new(context, Java.Lang.Class.FromType(kind switch
    {
        WidgetKind.BestNear => typeof(BestNearWidget),
        WidgetKind.Favorites => typeof(FavoritesWidget),
        _ => typeof(TripWidget),
    }));

    /// <summary>Which kind a placed widget is, from its class name; null for none of ours.</summary>
    public static WidgetKind? KindOf(string? className) => className switch
    {
        BestNearWidget.ClassName => WidgetKind.BestNear,
        FavoritesWidget.ClassName => WidgetKind.Favorites,
        TripWidget.ClassName => WidgetKind.Trip,
        _ => null,
    };
}

[BroadcastReceiver(Name = ClassName, Exported = false, Label = "@string/widget_best_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_best_info")]
public sealed class BestNearWidget : EssenceWidget
{
    public const string ClassName = "io.github.etiennex9.essence.widgets.BestNearWidget";
}

[BroadcastReceiver(Name = ClassName, Exported = false, Label = "@string/widget_favorites_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_favorites_info")]
public sealed class FavoritesWidget : EssenceWidget
{
    public const string ClassName = "io.github.etiennex9.essence.widgets.FavoritesWidget";
}

[BroadcastReceiver(Name = ClassName, Exported = false, Label = "@string/widget_trip_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_trip_info")]
public sealed class TripWidget : EssenceWidget
{
    public const string ClassName = "io.github.etiennex9.essence.widgets.TripWidget";
}
