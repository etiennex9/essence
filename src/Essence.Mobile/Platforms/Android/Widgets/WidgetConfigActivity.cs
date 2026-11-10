using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Essence.Mobile.Services;
using Essence.UI.App;
using Essence.UI.App.Widgets;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using RadioButton = Android.Widget.RadioButton;
using ScrollView = Android.Widget.ScrollView;
using View = Android.Views.View;

namespace Essence.Mobile.Widgets;

/// <summary>
/// A widget's settings, when it's added (Android 12 and later skip it: the defaults apply, and touching and holding the widget
/// reopens it): where Best near a place ranks from, or a frequent route's two places. The favorites widget has none.
/// </summary>
[Activity(
    Name = "io.github.etiennex9.essence.widgets.WidgetConfigActivity",
    Exported = true,
    Label = "@string/widget_config_title",
    Theme = "@style/Maui.MainTheme.NoActionBar")]
[IntentFilter([AppWidgetManager.ActionAppwidgetConfigure])]
public sealed class WidgetConfigActivity : AppCompatActivity
{
    private int _widgetId = AppWidgetManager.InvalidAppwidgetId;
    private IStringLocalizer<Strings> _l = null!;
    private IPreferenceStore _preferences = null!;
    private readonly List<(RadioGroup Group, Dictionary<int, WidgetPlaceRef> Choices)> _groups = [];
    private TextView? _error;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _widgetId = Intent?.Extras?.GetInt(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId) ?? AppWidgetManager.InvalidAppwidgetId;
        // Leaving without Done doesn't add the widget (before Android 12).
        SetResult(Result.Canceled, ResultIntent());
        var kind = EssenceWidget.KindOf(AppWidgetManager.GetInstance(this)?.GetAppWidgetInfo(_widgetId)?.Provider?.ClassName);
        if (_widgetId == AppWidgetManager.InvalidAppwidgetId || kind is null or WidgetKind.Favorites)
        {
            Finish();
            return;
        }

        AppCulture.Apply(this);
        var services = IPlatformApplication.Current!.Services;
        _l = services.GetRequiredService<IStringLocalizer<Strings>>();
        _preferences = services.GetRequiredService<IPreferenceStore>();
        var time = services.GetRequiredService<TimeProvider>();
        var places = new SavedPlaces(_preferences);
        places.LoadAsync().GetAwaiter().GetResult();
        var config = WidgetConfigs.ReadAsync(_preferences, _widgetId).AsTask().GetAwaiter().GetResult();
        var lastFix = LastLocation.ReadAsync(_preferences).AsTask().GetAwaiter().GetResult();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        root.SetFitsSystemWindows(true);
        root.AddView(Text(kind == WidgetKind.BestNear ? _l["BestNearYou"] : _l["BestOnTheWay"], 22, bold: true));

        var lastUsed = new Choice(
            lastFix is null ? _l["WidgetLastUsedOption"] : _l["WidgetLastUsedAgo", Ago(lastFix.At, time.GetUtcNow())],
            WidgetPlaceRef.LastUsed);
        var saved = places.Places.Select(p => new Choice(PlaceName(p), WidgetPlaceRef.For(p))).ToList();
        if (kind == WidgetKind.BestNear)
        {
            AddGroup(root, _l["WidgetRankFrom"], [.. saved, lastUsed], config.Origin ?? WidgetRefresher.DefaultOrigin(places));
        }
        else if (saved.Count == 0)
        {
            root.AddView(Text(_l["WidgetNoPlacesYet"], 16));
            root.AddView(ActionButton(_l["WidgetOpenPlaces"], OpenPlaces));
            SetContentView(new ScrollView(this) { FillViewport = true }.With(root));
            return;
        }
        else
        {
            var (from, to) = WidgetRefresher.DefaultRoute(config, places);
            AddGroup(root, _l["WidgetFrom"], [.. saved, lastUsed], from ?? saved[0].Place);
            AddGroup(root, _l["WidgetTo"], saved, to ?? saved[^1].Place);
        }

        _error = Text("", 15);
        _error.Visibility = ViewStates.Gone;
        root.AddView(_error);
        root.AddView(ActionButton(_l["WidgetAdd"], () => Save(kind.Value)));
        SetContentView(new ScrollView(this) { FillViewport = true }.With(root));
    }

    private void Save(WidgetKind kind)
    {
        var chosen = _groups.Select(g => g.Choices.GetValueOrDefault(g.Group.CheckedRadioButtonId)).ToList();
        var config = kind == WidgetKind.BestNear
            ? new WidgetConfig(Origin: chosen[0])
            : new WidgetConfig(From: chosen[0], To: chosen[1]);
        if (kind == WidgetKind.Trip && config.From == config.To)
        {
            _error!.Text = _l["WidgetSamePlace"];
            _error.Visibility = ViewStates.Visible;
            return;
        }

        WidgetConfigs.WriteAsync(_preferences, _widgetId, config).AsTask().GetAwaiter().GetResult();
        SetResult(Result.Ok, ResultIntent());
        WidgetUpdates.Schedule(this);
        WidgetUpdates.RefreshNow(this);
        Finish();
    }

    private void OpenPlaces()
    {
        var intent = new Intent(this, typeof(MainActivity))
            .SetAction(Intent.ActionView)!
            .SetData(Android.Net.Uri.Parse($"{DeepLinks.Scheme}://{DeepLinks.Host}/?view=places"))!
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        StartActivity(intent);
        Finish();
    }

    private void AddGroup(LinearLayout root, string label, List<Choice> choices, WidgetPlaceRef selected)
    {
        var title = Text(label, 14, bold: true);
        title.SetPadding(0, Dp(20), 0, Dp(4));
        root.AddView(title);
        // Ids unique in the whole screen: the From and To groups' choices would otherwise share them, and Android restores a
        // recreated screen's choices by id.
        var group = new RadioGroup(this) { Id = View.GenerateViewId() };
        var ids = new Dictionary<int, WidgetPlaceRef>();
        int? first = null, chosen = null;
        foreach (var choice in choices)
        {
            var button = new RadioButton(this) { Id = View.GenerateViewId(), Text = choice.Label, TextSize = 16 };
            button.SetMinHeight(Dp(48));
            group.AddView(button);
            ids[button.Id] = choice.Place;
            first ??= button.Id;
            if (choice.Place == selected)
            {
                chosen ??= button.Id;
            }
        }

        // The place set up, else the first.
        if ((chosen ?? first) is { } check)
        {
            group.Check(check);
        }
        root.AddView(group);
        _groups.Add((group, ids));
    }

    private string PlaceName(SavedPlace place) => place.Kind switch
    {
        PlaceKind.Home => _l["Home"],
        PlaceKind.Work => _l["Work"],
        _ => string.IsNullOrEmpty(place.Name) ? place.Address ?? _l["WidgetPlace"] : place.Name,
    };

    private string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var (unit, value) = Formatting.Ago(now - when);
        return unit switch
        {
            AgoUnit.JustNow => _l["AgoJustNow"],
            AgoUnit.Minutes => _l["AgoMinutes", value],
            AgoUnit.Hours => _l["AgoHours", value],
            _ => _l["AgoDays", value],
        };
    }

    private TextView Text(string text, float size, bool bold = false)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        if (bold)
        {
            view.SetTypeface(view.Typeface, Android.Graphics.TypefaceStyle.Bold);
        }

        return view;
    }

    private Android.Widget.Button ActionButton(string text, Action onClick)
    {
        var button = new Android.Widget.Button(this) { Text = text };
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(24) };
        button.LayoutParameters = layout;
        button.Click += (_, _) => onClick();
        return button;
    }

    private Intent ResultIntent() => new Intent().PutExtra(AppWidgetManager.ExtraAppwidgetId, _widgetId)!;

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1));

    private sealed record Choice(string Label, WidgetPlaceRef Place);
}

internal static class ViewExtensions
{
    public static ScrollView With(this ScrollView scroll, View child)
    {
        scroll.AddView(child);
        return scroll;
    }
}
