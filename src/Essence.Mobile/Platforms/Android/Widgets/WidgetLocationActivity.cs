using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.App;
using Essence.UI.App.Widgets;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Manifest = Android.Manifest;
using ScrollView = Android.Widget.ScrollView;

namespace Essence.Mobile.Widgets;

/// <summary>
/// Follow where I am: the one place the app asks for "Allow all the time", after saying what it's for (Android 11+ wants it
/// asked on its own, after the location itself, and opens its settings for it). Turning it on stores the choice
/// (<see cref="WidgetLocation.FollowKey"/>); turning it off forgets it. Opened from About and from a widget's settings, so
/// not exported. Each visit reads the state again: the permission may have changed in Android's settings meanwhile.
/// </summary>
[Activity(
    Name = "io.github.etiennex9.essence.widgets.WidgetLocationActivity",
    Exported = false,
    Label = "@string/widget_follow_title",
    Theme = "@style/Maui.MainTheme.NoActionBar")]
public sealed class WidgetLocationActivity : AppCompatActivity
{
    private const int AskLocation = 1;
    private const int AskBackground = 2;

    private IStringLocalizer<Strings> _l = null!;
    private IPreferenceStore _preferences = null!;
    private LinearLayout _state = null!;

    /// <summary>Turn on was pressed: back from Android's settings with the permission, Follow turns on.</summary>
    private bool _turningOn;

    /// <summary>Android answered the background request with a refusal without asking: only its settings can grant it now.</summary>
    private bool _refused;

    /// <summary>The same for the location itself (refused twice: Android no longer asks).</summary>
    private bool _locationRefused;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppCulture.Apply(this);
        var services = IPlatformApplication.Current!.Services;
        _l = services.GetRequiredService<IStringLocalizer<Strings>>();
        _preferences = services.GetRequiredService<IPreferenceStore>();
        _turningOn = savedInstanceState?.GetBoolean(nameof(_turningOn)) ?? false;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        root.AddView(Text(_l["WidgetFollowTitle"], 22, bold: true));
        root.AddView(Paragraph(_l["WidgetFollowDisclosure"], 16));
        root.AddView(Paragraph(_l["WidgetFollowHow"], 15));
        root.AddView(Paragraph(_l["WidgetFollowPrivacy"], 15));
        _state = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(_state);
        SetContentView(new ScrollView(this) { FillViewport = true }.With(root));
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean(nameof(_turningOn), _turningOn);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_turningOn && WidgetLocator.HasBackground(this))
        {
            TurnOn();
        }

        Show();
    }

    private void Show()
    {
        _state.RemoveAllViews();
        var follow = WidgetLocator.IsFollowOn();
        var hasLocation = WidgetLocator.HasLocation(this);
        var hasBackground = WidgetLocator.HasBackground(this);
        if (follow && hasBackground)
        {
            _state.AddView(Paragraph(_l["WidgetFollowOn"], 16, bold: true));
            _state.AddView(ActionButton(_l["WidgetFollowTurnOff"], TurnOff));
        }
        else if (!follow && hasBackground && OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            // Off, with "Allow all the time" still granted: Android's settings take it back.
            _state.AddView(ActionButton(_l["WidgetFollowTurnOn"], AskForBackground));
            _state.AddView(ActionButton(_l["WidgetFollowOpenSettings"], OpenSettings));
        }
        else
        {
            if (follow)
            {
                _state.AddView(Paragraph(_l["WidgetFollowPaused"], 16, bold: true));
            }

            if (!hasLocation)
            {
                _state.AddView(Paragraph(_l["WidgetFollowStep1"], 15));
                _state.AddView(ActionButton(_l["WidgetFollowAllowLocation"], AllowLocation));
                if (_locationRefused)
                {
                    _state.AddView(ActionButton(_l["WidgetFollowOpenSettings"], OpenSettings));
                }
            }
            else
            {
                if (!hasBackground)
                {
                    _state.AddView(Paragraph(_l["WidgetFollowStep2", AllTheTimeLabel()], 15));
                }

                _state.AddView(ActionButton(_l["WidgetFollowTurnOn"], AskForBackground));
                if (_refused)
                {
                    _state.AddView(ActionButton(_l["WidgetFollowOpenSettings"], () =>
                    {
                        _turningOn = true;
                        OpenSettings();
                    }));
                }
            }

            if (follow)
            {
                _state.AddView(ActionButton(_l["WidgetFollowTurnOff"], TurnOff));
            }
        }

        if (hasLocation && !WidgetLocator.HasPrecise(this))
        {
            _state.AddView(Paragraph(_l["WidgetFollowApproximate"], 15));
            _state.AddView(ActionButton(_l["WidgetFollowOpenSettings"], OpenSettings));
        }

        if (string.Equals(Build.Manufacturer, "samsung", StringComparison.OrdinalIgnoreCase))
        {
            _state.AddView(Paragraph(_l["WidgetFollowSamsung"], 15));
            _state.AddView(ActionButton(_l["WidgetFollowSamsungOpen"], OpenNeverSleeping));
        }

        _state.AddView(ActionButton(follow && hasBackground ? _l["Close"] : _l["WidgetFollowNotNow"], Finish));
    }

    private void AllowLocation() =>
        ActivityCompat.RequestPermissions(this, [Manifest.Permission.AccessCoarseLocation, Manifest.Permission.AccessFineLocation], AskLocation);

    private void AskForBackground()
    {
        if (WidgetLocator.HasBackground(this))
        {
            TurnOn();
            Show();
            return;
        }

        // Android 11+ opens its location settings for the app; Android 10 asks with a dialog. Either way, the answer comes back
        // in OnRequestPermissionsResult, and OnResume checks again.
        _turningOn = true;
        ActivityCompat.RequestPermissions(this, [WidgetLocator.BackgroundPermission], AskBackground);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == AskLocation && !WidgetLocator.HasLocation(this))
        {
            _locationRefused = true;
        }

        if (requestCode == AskBackground && !WidgetLocator.HasBackground(this))
        {
            // Refused, or refused for good (twice): Android's settings stay the way.
            _refused = true;
            _turningOn = false;
        }

        if (_turningOn && WidgetLocator.HasBackground(this))
        {
            TurnOn();
        }

        Show();
    }

    private void TurnOn()
    {
        _turningOn = false;
        WidgetLocation.SetFollowAsync(_preferences, true).AsTask().GetAwaiter().GetResult();
        WidgetUpdates.RefreshNow(this);
    }

    /// <summary>
    /// Stops following; "Allow all the time" stays until changed in Android's settings, which Show offers. (Giving it back with
    /// revokeSelfPermissionOnKill would take effect later, when Android stops the app, and silently undo a Turn on meanwhile.)
    /// </summary>
    private void TurnOff()
    {
        WidgetLocation.SetFollowAsync(_preferences, false).AsTask().GetAwaiter().GetResult();
        WidgetTracking.Update(this, wanted: false);
        WidgetUpdates.RefreshNow(this);
        Show();
    }

    /// <summary>Android's own name for the choice, in its language ("Allow all the time"), from Android 11.</summary>
    private string AllTheTimeLabel() =>
        OperatingSystem.IsAndroidVersionAtLeast(30) && PackageManager?.BackgroundPermissionOptionLabel is { Length: > 0 } label
            ? label
            : _l["WidgetFollowAllTheTime"];

    private void OpenSettings() =>
        StartActivity(new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings)
            .SetData(Android.Net.Uri.FromParts("package", PackageName, null))!);

    /// <summary>One UI's list of apps it never puts to sleep (Battery → Background usage limits), when it has one.</summary>
    private void OpenNeverSleeping()
    {
        try
        {
            StartActivity(new Intent("com.samsung.android.sm.ACTION_OPEN_CHECKABLE_LISTACTIVITY")
                .SetPackage("com.samsung.android.lool")!
                .PutExtra("activity_type", 2)!);
        }
        catch (ActivityNotFoundException)
        {
            OpenSettings();
        }
    }

    private TextView Paragraph(string text, float size, bool bold = false)
    {
        var view = Text(text, size, bold);
        view.SetPadding(0, Dp(16), 0, 0);
        return view;
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
        button.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(12),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1));
}
