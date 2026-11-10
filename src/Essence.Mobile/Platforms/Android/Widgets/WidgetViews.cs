using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using Essence.Mobile.Services;
using Essence.UI.App.Widgets;
using Color = Android.Graphics.Color;

namespace Essence.Mobile.Widgets;

/// <summary>A widget's views (layout/widget_essence.xml) from what <see cref="WidgetFeeds"/> answered.</summary>
internal static class WidgetViews
{
    private const int Rows = 5;

    private static readonly (int Row, int Logo, int Monogram, int Name, int Detail, int Price)[] RowIds =
    [
        (Resource.Id.row0, Resource.Id.row0_logo, Resource.Id.row0_monogram, Resource.Id.row0_name, Resource.Id.row0_detail, Resource.Id.row0_price),
        (Resource.Id.row1, Resource.Id.row1_logo, Resource.Id.row1_monogram, Resource.Id.row1_name, Resource.Id.row1_detail, Resource.Id.row1_price),
        (Resource.Id.row2, Resource.Id.row2_logo, Resource.Id.row2_monogram, Resource.Id.row2_name, Resource.Id.row2_detail, Resource.Id.row2_price),
        (Resource.Id.row3, Resource.Id.row3_logo, Resource.Id.row3_monogram, Resource.Id.row3_name, Resource.Id.row3_detail, Resource.Id.row3_price),
        (Resource.Id.row4, Resource.Id.row4_logo, Resource.Id.row4_monogram, Resource.Id.row4_name, Resource.Id.row4_detail, Resource.Id.row4_price),
    ];

    public static RemoteViews Build(Context context, WidgetKind kind, WidgetView view)
    {
        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_essence);
        views.SetTextViewText(Resource.Id.title, view.Title);
        views.SetTextViewText(Resource.Id.fuel, view.Fuel);
        views.SetTextViewText(Resource.Id.updated, view.Updated);
        views.SetOnClickPendingIntent(Resource.Id.widget_root, Open(context, view.Tap));
        views.SetOnClickPendingIntent(Resource.Id.refresh, Refresh(context, kind));

        for (var i = 0; i < Rows; i++)
        {
            var ids = RowIds[i];
            if (i >= view.Lines.Count)
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

    private static PendingIntent Refresh(Context context, WidgetKind kind)
    {
        var intent = new Intent().SetComponent(EssenceWidget.ComponentFor(context, kind))!.SetAction(EssenceWidget.RefreshAction)!;
        return PendingIntent.GetBroadcast(context, (int)kind, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }
}
