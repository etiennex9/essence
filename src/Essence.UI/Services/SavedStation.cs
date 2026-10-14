using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>What the favorites and the comparison show of a station before its prices load, saved when it's added.</summary>
public sealed record SavedStation(
    long Id, string? Brand, string? Name, string Address, string Region, double? Latitude, double? Longitude)
{
    public static SavedStation From(StationDetail station) => new(
        station.Id, station.Brand, station.Name, station.Address, station.Region, station.Latitude, station.Longitude);
}

/// <summary>Saves lists of stations as JSON in an <see cref="IPreferenceStore"/>. Entries can be null (the comparison's empty slots).</summary>
internal static class SavedStationStorage
{
    /// <summary>The saved list, or an empty one when there's none or it can't be read (edited by hand, an older format).</summary>
    public static async Task<List<SavedStation?>> ReadAsync(IPreferenceStore preferences, string key)
    {
        if (await preferences.GetAsync(key) is not { Length: > 0 } json)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, SavedStationJsonContext.Default.ListSavedStation) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static ValueTask WriteAsync(IPreferenceStore preferences, string key, IEnumerable<SavedStation?> stations) =>
        preferences.SetAsync(key, JsonSerializer.Serialize(stations.ToList(), SavedStationJsonContext.Default.ListSavedStation));
}

/// <summary>Source-generated (trim-safe) serialization of the saved stations.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<SavedStation?>))]
internal sealed partial class SavedStationJsonContext : JsonSerializerContext;
