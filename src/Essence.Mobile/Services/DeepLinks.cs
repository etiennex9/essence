using Essence.UI.App;
using Microsoft.AspNetCore.Components;

namespace Essence.Mobile.Services;

/// <summary>
/// Paths into the page from outside it: a home-screen widget's tap (<c>essence://open/?station=12</c>), or the page the app
/// was on when Android restores it. Only the map page with its own query parameters is accepted (<see cref="AppLinks"/>):
/// OpenStreetMap's <c>essence://osm-auth</c> goes to its own activity (OsmAuthCallbackActivity), never here.
/// </summary>
public sealed class DeepLinks
{
    public const string Scheme = AppLinks.Scheme;
    public const string Host = AppLinks.OpenHost;

    private string? _pending;

    /// <inheritdoc cref="AppLinks.PathOf"/>
    public static string? PathOf(Uri? link) => AppLinks.PathOf(link);

    /// <inheritdoc cref="AppLinks.IsMapPath"/>
    public static bool IsMapPath(string query) => AppLinks.IsMapPath(query);

    /// <summary>
    /// Opens <paramref name="path"/> in the running page, or keeps it for the page that's starting. Back from there returns to
    /// the home screen the widget was tapped on: the WebView, as Chrome, skips history entries added without a tap.
    /// </summary>
    public async Task OpenAsync(string path)
    {
        if (MainPage.Current is { } page && await page.TryNavigateAsync(path))
        {
            return;
        }

        _pending = path;
    }

    /// <summary>The app is starting from a link: the page it creates starts there.</summary>
    public void StartWith(string path) => _pending = path;

    /// <summary>
    /// A link that came while the page was still starting (TryDispatchAsync had no page yet): the page takes it as it starts
    /// (Routes.razor).
    /// </summary>
    public string? TakePending()
    {
        var path = _pending;
        _pending = null;
        return path;
    }

    /// <summary>The server changed: a link kept for the old one's stations is dropped.</summary>
    public void Clear() => _pending = null;

    /// <summary>Where a new page starts: a path kept by <see cref="OpenAsync"/> or <see cref="StartWith"/>, else <paramref name="fallback"/>.</summary>
    public string TakeStartPath(string fallback)
    {
        var path = _pending ?? fallback;
        _pending = null;
        return path;
    }
}

/// <summary>Navigation from native code, inside the page's own scope.</summary>
internal static class NavigationExtensions
{
    public static void Open(this NavigationManager navigation, string path) => navigation.NavigateTo(path.TrimStart('/'));
}
