namespace Essence.UI.App;

/// <summary>
/// The Android app's own links (<c>essence://</c>): a home-screen widget's into the page (<c>essence://open/?station=12</c>,
/// Essence.Mobile's DeepLinks), and OpenStreetMap's answer to its sign-in (<c>essence://osm-auth?code=…</c>), which goes to
/// MAUI's WebAuthenticator and never into the page. Pure, so the tests read them as the app does.
/// </summary>
public static class AppLinks
{
    public const string Scheme = "essence";

    /// <summary>The page's links: <c>essence://open/?…</c>.</summary>
    public const string OpenHost = "open";

    /// <summary>OpenStreetMap's sign-in answers here (<see cref="OsmAuthCallback"/>).</summary>
    public const string OsmAuthHost = "osm-auth";

    /// <summary>
    /// The redirect URI the OpenStreetMap application has registered for the Android app, exactly as it's sent: no slash
    /// after the host.
    /// </summary>
    public const string OsmAuthCallback = $"{Scheme}://{OsmAuthHost}";

    /// <summary>The query parameters the map page reads (MapPage.razor).</summary>
    private static readonly HashSet<string> Parameters = new(StringComparer.Ordinal) { "view", "station", "to", "from", "pick" };

    /// <summary>
    /// The page's URL relative to the app's, from a widget's <c>essence://open/…</c>; null for anything else, OpenStreetMap's
    /// <c>essence://osm-auth</c> included.
    /// </summary>
    public static string? PathOf(Uri? link)
    {
        if (link is null || link.Scheme != Scheme || link.Host != OpenHost || link.AbsolutePath is not ("/" or ""))
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
}
