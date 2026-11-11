using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.UI.Services;

namespace Essence.UI.App;

/// <summary>A location the device gave, and when the device took it (a fix kept by Android may be older than the ask).</summary>
public sealed record LastFix(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset At)
{
    [JsonIgnore]
    public GeoPoint Point => new(Latitude, Longitude);
}

/// <summary>
/// Where the device last was, as far as the Android app knows: it keeps every fix, the page's and the widgets' own (a tap on ↻,
/// and with Follow on, the refreshes and moves), so a widget ranking from where you are never waits for one.
/// </summary>
public static class LastLocation
{
    public const string StorageKey = "essence.last-location";

    public static async ValueTask<LastFix?> ReadAsync(IPreferenceStore preferences)
    {
        var json = await preferences.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.LastFix);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ValueTask WriteAsync(IPreferenceStore preferences, LastFix fix) =>
        preferences.SetAsync(StorageKey, JsonSerializer.Serialize(fix, AppJsonContext.Default.LastFix));

    /// <summary>
    /// Keeps <paramref name="fix"/> unless the one kept is newer: fixes come from the page, ↻, the refreshes and moves, and one
    /// Android kept for a while can arrive after a fresher one. True when it was kept.
    /// </summary>
    public static async ValueTask<bool> WriteIfNewerAsync(IPreferenceStore preferences, LastFix fix)
    {
        if (await ReadAsync(preferences) is { } kept && kept.At >= fix.At)
        {
            return false;
        }

        await WriteAsync(preferences, fix);
        return true;
    }
}
