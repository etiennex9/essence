using Android.App;
using Android.Content;
using Android.Content.PM;
using Essence.UI.App;

namespace Essence.Mobile;

/// <summary>
/// Where OpenStreetMap's sign-in comes back (<c>essence://osm-auth?code=…&amp;state=…</c>, MauiOsmSignIn): handed to MAUI's
/// WebAuthenticator, which finishes the sign-in. Its own host, so the page's links (<c>essence://open</c>, which only the
/// widgets send, straight to MainActivity) never get it, and nothing else does.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = AppLinks.Scheme,
    DataHost = AppLinks.OsmAuthHost)]
public class OsmAuthCallbackActivity : WebAuthenticatorCallbackActivity;
