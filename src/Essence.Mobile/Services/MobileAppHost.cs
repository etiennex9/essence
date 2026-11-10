using Essence.UI.App;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.Mobile.Services;

/// <summary>What the app adds to Essence.UI's page (<see cref="IAppHost"/>): the server, and newer builds.</summary>
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
    }

    public string ServerName => _servers.ServerId ?? "";

    public AppRelease? NewerRelease => _updates.Newer;

    public event Action? Changed
    {
        add => _updates.Changed += value;
        remove => _updates.Changed -= value;
    }

    public Task ChangeServerAsync() => MainThread.InvokeOnMainThreadAsync(() => App.Current.OpenServerPageAsync());

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
