using System.Globalization;
using Essence.UI.App;
using Essence.UI.App.Widgets;
using Essence.UI.Services;

namespace Essence.Mobile.Services;

/// <summary>
/// The app's preferences in Android's shared preferences, where the home-screen widgets read the same cars, places,
/// favorites and fuel. Thread-safe, so the widgets may read while the page writes. The device's locations go in a file of
/// their own, <see cref="LocationSharedName"/>, which Android's backups leave out (Resources/xml/backup_rules.xml and
/// data_extraction_rules.xml): they go to the server and nowhere else.
/// </summary>
public sealed class MauiPreferenceStore : IPreferenceStore
{
    public const string LocationSharedName = "essence.location";

    private static readonly HashSet<string> LocationKeys = [LastLocation.StorageKey, WidgetLocation.RankedFromKey];

    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(Preferences.Default.Get<string?>(key, null, SharedNameOf(key)));

    public ValueTask SetAsync(string key, string value)
    {
        Preferences.Default.Set(key, value, SharedNameOf(key));
        if (SharedNameOf(key) is not null)
        {
            // The first version kept the last location with the rest, which Android backs up.
            Preferences.Default.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    private static string? SharedNameOf(string key) => LocationKeys.Contains(key) ? LocationSharedName : null;
}

/// <summary>
/// The app's stand-in for a browser tab's sessionStorage: what the page keeps while it's "the same tab", the open trip above
/// all. Unlike the WebView's own storage, it outlives Android killing the app in the background while Google Maps navigates,
/// as a browser restores a discarded tab. <see cref="Clear"/> runs when the app starts afresh (MainActivity), and when the
/// server changes; anything older than <see cref="MaxAge"/> is forgotten anyway.
/// </summary>
public sealed class NativeSessionStore(TimeProvider time) : ISessionStore
{
    public const string SharedName = "essence.session";
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private const string SavedAtSuffix = "#at";

    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(Get(key));

    public string? Get(string key)
    {
        var value = Preferences.Default.Get<string?>(key, null, SharedName);
        var savedAt = Preferences.Default.Get<string?>(key + SavedAtSuffix, null, SharedName);
        return value is not null
               && long.TryParse(savedAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
               && time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(ms) <= MaxAge
            ? value
            : null;
    }

    public ValueTask SetAsync(string key, string value)
    {
        Preferences.Default.Set(key, value, SharedName);
        Preferences.Default.Set(key + SavedAtSuffix, time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), SharedName);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key)
    {
        Preferences.Default.Remove(key, SharedName);
        Preferences.Default.Remove(key + SavedAtSuffix, SharedName);
        return ValueTask.CompletedTask;
    }

    public void Clear() => Preferences.Default.Clear(SharedName);
}
