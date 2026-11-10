using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Essence.Mobile.Services;

namespace Essence.Mobile;

// SingleTop: a widget's tap reaches the open app (OnNewIntent) instead of stacking another.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        var services = IPlatformApplication.Current!.Services;
        // No saved state: the app starts afresh, as a new browser tab, so what the last one kept (an open trip) is gone. With
        // saved state, Android is restoring the app it stopped in the background, and the page picks up where it was.
        if (savedInstanceState is null)
        {
            services.GetRequiredService<NativeSessionStore>().Clear();
        }

        // A widget's link, only when it's what started the app now: restored or reopened from Recents, the activity gets its
        // first intent again, whose link would replace where the page was.
        if (savedInstanceState is null && Intent is { } intent && !intent.Flags.HasFlag(ActivityFlags.LaunchedFromHistory)
            && DeepLinks.PathOf(ToUri(intent.Data)) is { } path)
        {
            services.GetRequiredService<DeepLinks>().StartWith(path);
        }

        AppCulture.Apply(this);
        base.OnCreate(savedInstanceState);
        SystemBars.Update();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // Kept as the activity's intent, so a later recreation doesn't replay an older link (see OnCreate).
        Intent = intent;
        if (DeepLinks.PathOf(ToUri(intent?.Data)) is { } path)
        {
            _ = IPlatformApplication.Current!.Services.GetRequiredService<DeepLinks>().OpenAsync(path);
        }
    }

    private static Uri? ToUri(Android.Net.Uri? data) =>
        data?.ToString() is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
}
