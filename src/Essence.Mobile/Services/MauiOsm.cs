using Essence.UI.App;
using Essence.UI.Services;

namespace Essence.Mobile.Services;

/// <summary>
/// OpenStreetMap's sign-in for the Android app (<see cref="IOsmSignIn"/>): MAUI's WebAuthenticator, in a browser tab (a
/// Custom Tab where there's one), back through <c>essence://osm-auth</c>, which OsmAuthCallbackActivity takes; the page's
/// own links (<c>essence://open</c>, DeepLinks) never see it.
/// </summary>
public sealed class MauiOsmSignIn : IOsmSignIn
{
    public string RedirectUri => AppLinks.OsmAuthCallback;

    public async Task<OsmCallback?> AuthorizeAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        try
        {
            var result = await MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions { Url = authorizeUrl, CallbackUrl = new Uri(AppLinks.OsmAuthCallback) })).WaitAsync(cancellationToken);
            return new OsmCallback(Get(result, "code"), Get(result, "state"), Get(result, "error"));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The user came back from the tab without signing in.
            return null;
        }
    }

    private static string? Get(WebAuthenticatorResult result, string name) =>
        result.Properties.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
}

/// <summary>
/// The linked OpenStreetMap account in Android's secure storage (<see cref="IOsmTokenStore"/>), encrypted with a key that
/// never leaves the device: never in the preferences the widgets read, nor in Android's backups
/// (Resources/xml/backup_rules.xml and data_extraction_rules.xml leave the file out). Not per server: the account is the
/// person's.
/// </summary>
public sealed class SecureOsmTokenStore : IOsmTokenStore
{
    public const string StorageKey = "essence.osm";

    public async ValueTask<string?> GetAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(StorageKey);
        }
        catch (Exception)
        {
            // A key Android no longer has (restored from elsewhere, the lock screen reset): link again.
            SecureStorage.Default.Remove(StorageKey);
            return null;
        }
    }

    public async ValueTask SetAsync(string value)
    {
        if (value.Length == 0)
        {
            SecureStorage.Default.Remove(StorageKey);
            return;
        }

        await SecureStorage.Default.SetAsync(StorageKey, value);
    }
}
