using System.Globalization;
using Essence.UI.App;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.Mobile.Services;

/// <summary>
/// What the app adds to Essence.UI's page (<see cref="IAppHost"/>): its version name, the server, newer builds, and whether
/// the widgets follow where you are.
/// </summary>
public sealed class MobileAppHost : IAppHost
{
    private readonly ServerSettings _servers;
    private readonly AppUpdateCheck _updates;
    private readonly BuildInfo _build;
    private readonly IStringLocalizer<Strings> _l;
    private int _toldTooOld;

    public MobileAppHost(ServerSettings servers, AppUpdateCheck updates, BuildInfo build, AppHttpHandler http, IStringLocalizer<Strings> l)
    {
        _servers = servers;
        _updates = updates;
        _build = build;
        _l = l;
        http.TooOld += OnTooOld;
        _updates.Changed += OnChanged;
    }

    // CI's builds have a version code above 1 (README → Versions); a local or pull request build has 1, and version 0.0.0.
    public string? VersionName { get; } =
        long.TryParse(AppInfo.Current.BuildString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code > 1
            ? AppInfo.Current.VersionString
            : null;

    public string ServerName => _servers.ServerId ?? "";

    public AppRelease? NewerRelease => _updates.Newer;

    public event Action? Changed;

    public WidgetFollowState? WidgetsFollow => Widgets.WidgetLocator.FollowState(Platform.AppContext);

    public Task ChangeServerAsync() => MainThread.InvokeOnMainThreadAsync(() => App.Current.OpenServerPageAsync());

    public Task OpenWidgetsFollowAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (Platform.CurrentActivity is { } activity)
        {
            activity.StartActivity(new Android.Content.Intent(activity, typeof(Widgets.WidgetLocationActivity)));
        }
    });

    /// <summary>The app came back: Follow may have been turned on or off, or its permission changed in Android's settings.</summary>
    public void OnResumed() => OnChanged();

    private void OnChanged() => Changed?.Invoke();

    /// <summary>The server refused this build (426): says so once while the app runs, with where to get a newer one.</summary>
    private void OnTooOld()
    {
        if (Volatile.Read(ref _toldTooOld) == 1)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // Without a window (a widget's refresh), the next 426 seen with one says it.
            if (Application.Current?.Windows.FirstOrDefault()?.Page is not { } page || Interlocked.Exchange(ref _toldTooOld, 1) == 1)
            {
                return;
            }

            var releases = string.IsNullOrEmpty(_build.Repository) ? null : new Uri($"https://github.com/{_build.Repository}/releases/latest");
            if (releases is null)
            {
                await page.DisplayAlertAsync(_l["AppTooOldTitle"], _l["AppTooOldMessage"], _l["Close"]);
                return;
            }

            if (await page.DisplayAlertAsync(_l["AppTooOldTitle"], _l["AppTooOldMessage"], _l["AppUpdateDownload"], _l["AppTooOldLater"]))
            {
                await Launcher.Default.OpenAsync(releases);
            }
        });
    }
}
