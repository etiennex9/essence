using Essence.UI.Services;

namespace Essence.UI.App;

/// <summary>
/// Keeps what names stations per server: a station's id is its row in that server's database, so another server's id 12 is
/// another station. Favorites, the comparison and the corrected hours get the server in their key
/// (<c>essence.favorites@example.com</c>); cars, places and the fuel are the driver's own and stay shared, and so does the
/// linked OpenStreetMap account, which isn't kept here at all (<see cref="IOsmTokenStore"/>).
/// </summary>
/// <param name="serverId">The current server (<see cref="ServerAddress.DisplayName"/> of its API); null or empty before one is chosen.</param>
public sealed class ServerScopedPreferences(IPreferenceStore inner, Func<string?> serverId) : IPreferenceStore
{
    /// <summary>The keys kept per server.</summary>
    public static IReadOnlySet<string> ScopedKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FavoriteStations.StorageKey,
        ComparedStations.StorageKey,
        StationHoursOverrides.StorageKey,
    };

    public ValueTask<string?> GetAsync(string key) => inner.GetAsync(KeyFor(key));

    public ValueTask SetAsync(string key, string value) => inner.SetAsync(KeyFor(key), value);

    /// <summary>The key as stored.</summary>
    public string KeyFor(string key) =>
        ScopedKeys.Contains(key) && serverId() is { Length: > 0 } server ? $"{key}@{server.ToLowerInvariant()}" : key;
}
