using Microsoft.AspNetCore.Components;

namespace Essence.Mobile.Services;

/// <summary>
/// Paths into the page from outside it: a home-screen widget's tap (<c>essence://open/?station=12</c>), or the page the app
/// was on when Android restores it. Only the map page with its own query parameters is accepted.
/// </summary>
public sealed class DeepLinks
{
    public const string Scheme = "essence";
    public const string Host = "open";

    /// <summary>The query parameters the map page reads (MapPage.razor).</summary>
    private static readonly HashSet<string> Parameters = new(StringComparer.Ordinal) { "view", "station", "to", "from", "pick" };

    private string? _pending;

    /// <summary>The page's URL relative to the app's, from a widget's <c>essence://open/…</c>; null for anything else.</summary>
    public static string? PathOf(Uri? link)
    {
        if (link is null || link.Scheme != Scheme || link.Host != Host || link.AbsolutePath is not ("/" or ""))
        {
            return null;
        }

        return IsMapPath(link.Query) ? "/" + link.Query : null;
    }

    /// <summary>Whether a saved relative URL (<c>?view=trip</c>) is the map page with only its own parameters.</summary>
    public static bool IsMapPath(string query)
    {
        var parts = query.TrimStart('?');
        return parts.Length == 0
               || parts.Split('&').All(p => p.Split('=', 2) is [var name, _] && Parameters.Contains(Uri.UnescapeDataString(name)));
    }

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
