using Essence.Mobile.Components;
using Essence.Mobile.Services;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;
using MapHost = Essence.Mobile.MainPage;

namespace Essence.Mobile;

/// <summary>
/// One window: the server screen until a server is chosen, then the map page. Changing server opens the server screen over
/// the map, and a server that answers gets a new map page, with a new scope and API client (MauiProgram).
/// </summary>
public partial class App : Application
{
    private readonly ServerSettings _servers;
    private readonly NativeSessionStore _session;
    private readonly DeepLinks _links;
    private readonly AppUpdateCheck _updates;
    private readonly IStringLocalizer<Strings> _l;

    /// <param name="host">Taken here so it listens for a server's 426 from the start, before About asks for it.</param>
    public App(ServerSettings servers, NativeSessionStore session, DeepLinks links, AppUpdateCheck updates, IStringLocalizer<Strings> l, MobileAppHost host)
    {
        InitializeComponent();
        _servers = servers;
        _session = session;
        _links = links;
        _updates = updates;
        _l = l;
        RequestedThemeChanged += (_, _) => SystemBars.Update();
    }

    public static new App Current => (App)Application.Current!;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_servers.Current is null ? NewServerPage(changing: false) : NewMainPage()) { Title = "Essence" };
        window.Created += (_, _) => _ = _updates.CheckAsync();
        window.Stopped += (_, _) =>
        {
            MapHost.Current?.OnAppStopped();
            // Cars, places, favorites or the fuel may have changed.
            Widgets.WidgetUpdates.RefreshNow(Platform.AppContext);
        };
        window.Resumed += (_, _) =>
        {
            MapHost.Current?.OnAppResumed();
            _ = _updates.CheckAsync();
        };
        // Back on the map's first page closes the activity, but not the process: the next launch makes a new window, and the
        // old page would keep its WebView and scope alive (and its timers) for as long as the process lives.
        window.Destroying += (_, _) => (window.Page as MapHost)?.Detach();
        return window;
    }

    /// <summary>The server screen over the map, from About → Server → Change.</summary>
    public async Task OpenServerPageAsync()
    {
        if (Windows.FirstOrDefault()?.Page is { } page && page.Navigation.ModalStack.Count == 0)
        {
            await page.Navigation.PushModalAsync(NewServerPage(changing: true));
        }
    }

    /// <summary>After the server screen: back to the map, a new one if the server changed (or there was none yet).</summary>
    public async Task ShowMapAsync(bool newPage)
    {
        if (Windows.FirstOrDefault() is not { } window)
        {
            return;
        }

        if (window.Page is { } page && page.Navigation.ModalStack.Count > 0)
        {
            await page.Navigation.PopModalAsync(animated: !newPage);
        }

        if (newPage)
        {
            _links.Clear();
        }

        if (newPage || window.Page is not MapHost)
        {
            // Disposes the old page's scope: its timers and refreshes stop asking the old server.
            MapHost.Current?.Detach();
            window.Page = NewMainPage();
        }
    }

    private ServerPage NewServerPage(bool changing) => new(_servers, _l, changing);

    /// <summary>
    /// Starts where a widget asked, else where the page was when Android last stopped the app (a browser restoring its tab),
    /// else the map.
    /// </summary>
    private MapHost NewMainPage()
    {
        var saved = _session.Get(Routes.UrlKey);
        var restored = saved is not null && DeepLinks.IsMapPath(saved) ? "/" + saved.TrimStart('/') : "/";
        return new MapHost(_links.TakeStartPath(restored));
    }
}
