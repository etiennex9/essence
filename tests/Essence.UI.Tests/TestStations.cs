using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

/// <summary>Stations and storage for the favorites and comparison tests.</summary>
internal static class TestStations
{
    public static SavedStation Station(long id, string brand = "Shell") => SavedStation.From(Detail(id, brand));

    public static StationDetail Detail(long id, string brand = "Shell") => new(
        id, $"{brand} {id}", brand, "En opération", $"{id} rue Principale", "H2X3J4", "Montréal", 45.5, -73.56, true,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);
}

/// <summary>An <see cref="IPreferenceStore"/> in memory.</summary>
internal sealed class MemoryPreferences : IPreferenceStore
{
    private readonly Dictionary<string, string> _values = [];

    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(_values.GetValueOrDefault(key));

    public ValueTask SetAsync(string key, string value)
    {
        _values[key] = value;
        return ValueTask.CompletedTask;
    }
}
