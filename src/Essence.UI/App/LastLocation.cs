using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.UI.Services;

namespace Essence.UI.App;

/// <summary>A location the device gave, and when.</summary>
public sealed record LastFix(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset At)
{
    [JsonIgnore]
    public GeoPoint Point => new(Latitude, Longitude);
}

/// <summary>
/// Where the app last saw the device. The Android app keeps it with every fix, so a home-screen widget can rank from "where I
/// last used Essence" without asking for the location in the background.
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
}
