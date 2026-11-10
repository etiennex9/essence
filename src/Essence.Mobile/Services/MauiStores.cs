using System.Globalization;
using Essence.UI.Services;

namespace Essence.Mobile.Services;

/// <summary>
/// The app's preferences in Android's shared preferences, where the home-screen widgets read the same cars, places,
/// favorites and fuel. Thread-safe, so the widgets may read while the page writes.
/// </summary>
public sealed class MauiPreferenceStore : IPreferenceStore
{
    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(Preferences.Default.Get<string?>(key, null));

    public ValueTask SetAsync(string key, string value)
    {
        Preferences.Default.Set(key, value);
        return ValueTask.CompletedTask;
    }
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
