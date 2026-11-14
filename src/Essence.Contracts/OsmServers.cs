namespace Essence.Contracts;

/// <summary>
/// The OpenStreetMap servers a deployment may name (<see cref="ClientConfig"/>), checked by the API as it starts and by the
/// apps, which don't take a server's word for it: the Android app's server can be anyone's, and a device's token goes to
/// these addresses. https only, but on the device itself (a local OpenStreetMap); and the API that takes the edits must be
/// the sign-in's own: on the same host (OpenStreetMap's development server), or OpenStreetMap's own pair, www. and api.
/// Shared by the API and the UI.
/// </summary>
public static class OsmServers
{
    /// <summary>OpenStreetMap's own hosts: its pages and sign-in, and its API.</summary>
    private static readonly string[] OpenStreetMapHosts = ["www.openstreetmap.org", "openstreetmap.org", "api.openstreetmap.org"];

    /// <summary>An address a token may go to: https, or http on the device itself (localhost, 127.0.0.1, ::1).</summary>
    public static bool IsSafe(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    /// <summary>
    /// <paramref name="api"/> is the API of the OpenStreetMap that signs in at <paramref name="auth"/>, both safe: the same
    /// scheme, host and port, or both OpenStreetMap's own (<c>www.openstreetmap.org</c> and <c>api.openstreetmap.org</c>).
    /// </summary>
    public static bool ApiBelongsTo(Uri auth, Uri api) =>
        IsSafe(auth) && IsSafe(api)
        && ((auth.Scheme == api.Scheme && string.Equals(auth.Host, api.Host, StringComparison.OrdinalIgnoreCase) && auth.Port == api.Port)
            || (IsOpenStreetMap(auth) && IsOpenStreetMap(api)));

    private static bool IsOpenStreetMap(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && OpenStreetMapHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
}
