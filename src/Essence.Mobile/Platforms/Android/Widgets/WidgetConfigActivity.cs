using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.App;
using Essence.Mobile.Services;
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
/// reopens it): where Best near a place ranks from, or a frequent route's two places. The favorites widget has none. With
/// Where I am chosen, it says how up to date that is, offers to keep it so (<see cref="WidgetLocationActivity"/>), and asks for
/// the location when the app has never had it.
/// </summary>
[Activity(
    Name = "io.github.etiennex9.essence.widgets.WidgetConfigActivity",
    Exported = true,
    Label = "@string/widget_config_title",
    Theme = "@style/Maui.MainTheme.NoActionBar")]
[IntentFilter([AppWidgetManager.ActionAppwidgetConfigure])]
public sealed class WidgetConfigActivity : AppCompatActivity
{
    private const int AskLocation = 1;

    private int _widgetId = AppWidgetManager.InvalidAppwidgetId;
    private WidgetKind _kind;
    private IStringLocalizer<Strings> _l = null!;
    private IPreferenceStore _preferences = null!;
    private readonly List<(RadioGroup Group, Dictionary<int, WidgetPlaceRef> Choices)> _groups = [];
    private TextView? _error;
    private LinearLayout? _youNote;
    private TextView? _youState;
    private Android.Widget.Button? _keepUpToDate;

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

        _kind = kind.Value;
        AppCulture.Apply(this);
        var services = IPlatformApplication.Current!.Services;
        _l = services.GetRequiredService<IStringLocalizer<Strings>>();
        _preferences = services.GetRequiredService<IPreferenceStore>();
        var places = new SavedPlaces(_preferences);
        places.LoadAsync().GetAwaiter().GetResult();
        var config = WidgetConfigs.ReadAsync(_preferences, _widgetId).AsTask().GetAwaiter().GetResult();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        root.AddView(Text(kind == WidgetKind.BestNear ? _l["BestNearYou"] : _l["BestOnTheWay"], 22, bold: true));

        var you = new Choice(_l["WidgetYouOption"], WidgetPlaceRef.You);
        var saved = places.Places.Select(p => new Choice(PlaceName(p), WidgetPlaceRef.For(p))).ToList();
        if (kind == WidgetKind.BestNear)
        {
            AddGroup(root, _l["WidgetRankFrom"], [you, .. saved], config.Origin ?? WidgetRefresher.DefaultOrigin);
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
            AddGroup(root, _l["WidgetFrom"], [you, .. saved], from ?? saved[0].Place);
            AddGroup(root, _l["WidgetTo"], saved, to ?? saved[^1].Place);
        }

        root.AddView(YouNote());
        _error = Text("", 15);
        _error.Visibility = ViewStates.Gone;
        root.AddView(_error);
        root.AddView(ActionButton(_l["WidgetAdd"], Save));
        SetContentView(new ScrollView(this) { FillViewport = true }.With(root));
    }

    /// <summary>Back from turning Follow on, or from Android's settings: the note says what's true now.</summary>
    protected override void OnResume()
    {
        base.OnResume();
        UpdateYouNote();
    }

    /// <summary>Under the choices, while Where I am is chosen: how up to date it is, and the way to keep it so.</summary>
    private LinearLayout YouNote()
    {
        _youNote = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _youNote.SetPadding(0, Dp(12), 0, 0);
        _youState = Text("", 15);
        _youNote.AddView(_youState);
        _keepUpToDate = ActionButton(_l["WidgetKeepUpToDate"], () => StartActivity(new Intent(this, typeof(WidgetLocationActivity))));
        ((LinearLayout.LayoutParams)_keepUpToDate.LayoutParameters!).TopMargin = Dp(8);
        _youNote.AddView(_keepUpToDate);
        foreach (var (group, _) in _groups)
        {
            group.CheckedChange += (_, _) => UpdateYouNote();
        }

        return _youNote;
    }

    private void UpdateYouNote()
    {
        if (_youNote is null || _youState is null || _keepUpToDate is null)
        {
            return;
        }

        _youNote.Visibility = Chosen().Any(c => c?.IsYou == true) ? ViewStates.Visible : ViewStates.Gone;
        var access = WidgetLocator.Access(this);
        _youState.Text = access switch
        {
            WidgetLocationAccess.AllTheTime => _l["WidgetYouKept"],
            WidgetLocationAccess.NotAllowed => _l["WidgetYouWillAsk"],
            _ when WidgetLocator.IsFollowOn() => _l["WidgetYouPaused"],
            _ => _l["WidgetYouOnTap"],
        };
        _keepUpToDate.Visibility = BuildInfo.Current.BackgroundLocation && access != WidgetLocationAccess.AllTheTime
            ? ViewStates.Visible
            : ViewStates.Gone;
    }

    private List<WidgetPlaceRef?> Chosen() => [.. _groups.Select(g => g.Choices.GetValueOrDefault(g.Group.CheckedRadioButtonId))];

    /// <summary>
    /// Saves the choices; with Where I am chosen and no location allowed yet, asks for it first (and saves whatever the
    /// answer: the widget then says to allow it).
    /// </summary>
    private void Save()
    {
        var chosen = Chosen();
        if (_kind == WidgetKind.Trip && chosen[0] == chosen[1])
        {
            _error!.Text = _l["WidgetSamePlace"];
            _error.Visibility = ViewStates.Visible;
            return;
        }

        if (chosen.Any(c => c?.IsYou == true) && !WidgetLocator.HasLocation(this))
        {
            ActivityCompat.RequestPermissions(
                this, [Android.Manifest.Permission.AccessCoarseLocation, Android.Manifest.Permission.AccessFineLocation], AskLocation);
            return;
        }

        SaveAndClose(chosen);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == AskLocation)
        {
            SaveAndClose(Chosen());
        }
    }

    private void SaveAndClose(List<WidgetPlaceRef?> chosen)
    {
        var config = _kind == WidgetKind.BestNear
            ? new WidgetConfig(Origin: chosen[0])
            : new WidgetConfig(From: chosen[0], To: chosen[1]);
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
    /// <summary>
    /// The screen's scrolling content, kept clear of the system bars: the scroll view takes their insets as its padding, which
    /// would replace the content's own margins if the content took them.
    /// </summary>
    public static ScrollView With(this ScrollView scroll, View child)
    {
        scroll.SetFitsSystemWindows(true);
        scroll.AddView(child);
        return scroll;
    }
}
