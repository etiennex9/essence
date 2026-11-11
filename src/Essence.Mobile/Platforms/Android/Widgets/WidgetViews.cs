using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using SizeF = Android.Util.SizeF;
using Android.Views;
using Android.Widget;
using Essence.Mobile.Services;
using Essence.UI.App.Widgets;
using Color = Android.Graphics.Color;

namespace Essence.Mobile.Widgets;

/// <summary>
/// A widget's views (layout/widget_essence.xml) from what <see cref="WidgetFeeds"/> answered, with only the stations that fit
/// whole in the widget's height: a smaller widget leaves out the last ones (the dearest favorites) rather than cut one.
/// </summary>
internal static class WidgetViews
{
    private const int Rows = 5;

    /// <summary>A station's row in widget_essence.xml: its two lines of text and 6 dp above and below.</summary>
    private const int RowDp = 50;

    /// <summary>Around the rows: the widget's padding, the header and the footer with ↻.</summary>
    private const int ChromeDp = 84;

    /// <summary>The narrowest the widgets get (widget_*_info.xml's minResizeWidth).</summary>
    private const int MinWidthDp = 180;

    private static readonly (int Row, int Logo, int Monogram, int Name, int Detail, int Price)[] RowIds =
    [
        (Resource.Id.row0, Resource.Id.row0_logo, Resource.Id.row0_monogram, Resource.Id.row0_name, Resource.Id.row0_detail, Resource.Id.row0_price),
        (Resource.Id.row1, Resource.Id.row1_logo, Resource.Id.row1_monogram, Resource.Id.row1_name, Resource.Id.row1_detail, Resource.Id.row1_price),
        (Resource.Id.row2, Resource.Id.row2_logo, Resource.Id.row2_monogram, Resource.Id.row2_name, Resource.Id.row2_detail, Resource.Id.row2_price),
        (Resource.Id.row3, Resource.Id.row3_logo, Resource.Id.row3_monogram, Resource.Id.row3_name, Resource.Id.row3_detail, Resource.Id.row3_price),
        (Resource.Id.row4, Resource.Id.row4_logo, Resource.Id.row4_monogram, Resource.Id.row4_name, Resource.Id.row4_detail, Resource.Id.row4_price),
    ];

    /// <param name="locate">↻ finds where you are before refreshing (<see cref="WidgetLocationService"/>).</param>
    /// <param name="options">The widget's size as the launcher last gave it (AppWidgetManager.GetAppWidgetOptions).</param>
    public static RemoteViews Build(Context context, WidgetKind kind, WidgetView view, bool locate, Bundle? options)
    {
        if (view.Lines.Count <= 1)
        {
            return Build(context, kind, view, locate, rows: view.Lines.Count);
        }

        // Android 12+: one layout per number of rows, and the launcher shows the largest that fits, as the widget is resized.
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var sizes = new Dictionary<SizeF, RemoteViews>();
            for (var rows = 1; rows <= view.Lines.Count; rows++)
            {
                // One row whatever the height: the smallest layout is the one shown when none fits.
                sizes[new SizeF(MinWidthDp, rows == 1 ? 1 : HeightFor(rows))] = Build(context, kind, view, locate, rows);
            }

            return new RemoteViews(sizes);
        }

        // Before, the height the launcher gave, built again when it changes (EssenceWidget.OnAppWidgetOptionsChanged).
        return Build(context, kind, view, locate, RowsThatFit(options?.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight) ?? 0, view.Lines.Count));
    }

    /// <summary>How many rows fit whole in <paramref name="heightDp"/>: at least one, all of them when the height isn't known.</summary>
    public static int RowsThatFit(int heightDp, int lines) =>
        heightDp <= 0 ? lines : Math.Clamp((heightDp - ChromeDp) / RowDp, 1, lines);

    private static int HeightFor(int rows) => ChromeDp + (rows * RowDp);

    private static RemoteViews Build(Context context, WidgetKind kind, WidgetView view, bool locate, int rows)
    {
        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_essence);
        views.SetTextViewText(Resource.Id.title, view.Title);
        views.SetTextViewText(Resource.Id.fuel, view.Fuel);
        views.SetTextViewText(Resource.Id.updated, view.Updated);
        views.SetOnClickPendingIntent(Resource.Id.widget_root, Open(context, view.Tap));
        views.SetOnClickPendingIntent(Resource.Id.refresh, locate ? Locate(context, kind) : Refresh(context, kind));

        for (var i = 0; i < Rows; i++)
        {
            var ids = RowIds[i];
            if (i >= view.Lines.Count || i >= rows)
            {
                views.SetViewVisibility(ids.Row, ViewStates.Gone);
                continue;
            }

            var line = view.Lines[i];
            views.SetViewVisibility(ids.Row, ViewStates.Visible);
            views.SetInt(ids.Logo, "setColorFilter", Color.ParseColor(line.Brand.Background).ToArgb());
            views.SetTextViewText(ids.Monogram, line.Brand.Monogram);
            views.SetTextColor(ids.Monogram, Color.ParseColor(line.Brand.Foreground));
            views.SetTextViewText(ids.Name, line.Name);
            views.SetTextViewText(ids.Detail, line.Detail);
            views.SetTextViewText(ids.Price, line.Price);
            views.SetContentDescription(ids.Row, $"{line.Name}, {line.Price}, {line.Detail}");
            views.SetOnClickPendingIntent(ids.Row, Open(context, line.Tap));
        }

        views.SetViewVisibility(Resource.Id.message, view.Message is null ? ViewStates.Gone : ViewStates.Visible);
        views.SetTextViewText(Resource.Id.message, view.Message ?? "");
        return views;
    }

    /// <summary>
    /// Opens the app on a path of the map page (DeepLinks). Each path is its own intent (by its data), so the rows keep their
    /// own taps. Another app could open the same paths, MainActivity being the launcher's: they only show a station or a list,
    /// or start a trip to a saved place, and DeepLinks accepts nothing else.
    /// </summary>
    private static PendingIntent Open(Context context, string tap)
    {
        var intent = new Intent(context, typeof(MainActivity))
            .SetAction(Intent.ActionView)!
            .SetData(Android.Net.Uri.Parse($"{DeepLinks.Scheme}://{DeepLinks.Host}{tap}"))!
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop)!;
        return PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>
    /// Starts the location service itself, not through a broadcast: Android lets a foreground service use the location when a
    /// tap on a widget starts it, an exemption a hop through the app may lose.
    /// </summary>
    private static PendingIntent Locate(Context context, WidgetKind kind) => PendingIntent.GetForegroundService(
        context, (int)kind, new Intent(context, typeof(WidgetLocationService)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    private static PendingIntent Refresh(Context context, WidgetKind kind)
    {
        var intent = new Intent().SetComponent(EssenceWidget.ComponentFor(context, kind))!.SetAction(EssenceWidget.RefreshAction)!;
        return PendingIntent.GetBroadcast(context, (int)kind, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }
}
