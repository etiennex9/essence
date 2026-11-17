using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.Contracts;
using Essence.Hours;

namespace Essence.UI.Services;

/// <summary>A station's hours as the user corrected them on the device (<see cref="StationHoursOverrides"/>).</summary>
/// <param name="Rule">The canonical opening_hours (<see cref="OpeningHours.ToRule"/>), in the station's time zone.</param>
/// <param name="SavedAt">When it was saved.</param>
/// <param name="Name">The station's name as shown then, to tell the corrections apart where a list of them shows.</param>
/// <param name="Address">Its address then, likewise.</param>
public sealed record HoursCorrection(string Rule, DateTimeOffset SavedAt, string? Name = null, string? Address = null);

/// <summary>
/// The stations' hours the user corrected, kept on the device through <see cref="IPreferenceStore"/>: they always win over
/// the server's (<see cref="Apply"/>), for what the details, the map, the rows, Best's arrivals, the Go links and the
/// widgets say, and Best near you and Best on the way send their ids as <c>ignoreHours</c> so the server never leaves out a
/// station the user says is open. Per server (<see cref="App.ServerScopedPreferences"/>): a station's id is its row in that
/// server's database.
/// </summary>
public sealed class StationHoursOverrides(IPreferenceStore preferences, TimeProvider time)
{
    public const string StorageKey = "essence.hours";

    /// <summary>The source the corrected hours are shown with (<see cref="StationHours.Source"/>): "Your hours".</summary>
    public const string Source = "device";

    private readonly Dictionary<long, HoursCorrection> _corrections = [];
    private Task? _loading;

    /// <summary>Raised after any change, including the saved corrections coming in.</summary>
    public event Action? Changed;

    public int Count => _corrections.Count;

    /// <summary>The corrected stations, the latest saved first: what <c>ignoreHours</c> sends, its first ones past its limit.</summary>
    public IReadOnlyList<long> StationIds =>
        [.. _corrections.OrderByDescending(c => c.Value.SavedAt).ThenBy(c => c.Key).Select(c => c.Key)];

    /// <summary>Reads the saved corrections once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>The station's correction; null when its hours aren't corrected.</summary>
    public HoursCorrection? Find(long stationId) => _corrections.GetValueOrDefault(stationId);

    public bool Has(long stationId) => _corrections.ContainsKey(stationId);

    /// <summary>
    /// The hours the app goes by for a station: the user's correction, as hours from <see cref="Source"/> that are trusted
    /// and open as they say (whatever the server says of the station being closed for now), else the server's.
    /// </summary>
    public StationHours? Apply(long stationId, StationHours? hours) =>
        _corrections.TryGetValue(stationId, out var correction)
            ? new StationHours(correction.Rule, Source, Trusted: true, Confirmed: true, correction.SavedAt, Link: null)
            : hours;

    /// <summary>Saves the station's hours as the user set them, in place of a correction it had.</summary>
    public async Task SaveAsync(long stationId, OpeningHours hours, string? name, string? address)
    {
        await LoadAsync();
        var correction = new HoursCorrection(hours.ToRule(), time.GetUtcNow(), name, address);
        if (_corrections.TryGetValue(stationId, out var saved) && saved with { SavedAt = correction.SavedAt } == correction)
        {
            return;
        }

        _corrections[stationId] = correction;
        await SaveCoreAsync();
        Changed?.Invoke();
    }

    /// <summary>Forgets the station's correction: its hours are the server's again ("Use the station's hours").</summary>
    public async Task ResetAsync(long stationId)
    {
        await LoadAsync();
        if (!_corrections.Remove(stationId))
        {
            return;
        }

        await SaveCoreAsync();
        Changed?.Invoke();
    }

    /// <summary>
    /// <c>{"12": {"rule": "Mo-Fr 06:00-21:30", "savedAt": "…", "name": "Costco", "address": "…"}}</c>, read leniently: what
    /// can't be read (storage edited by hand, a newer version's) is left out, one station at a time, and a rule is made
    /// canonical again.
    /// </summary>
    private async Task LoadCoreAsync()
    {
        if (await preferences.GetAsync(StorageKey) is not { Length: > 0 } json)
        {
            return;
        }

        Dictionary<string, JsonElement>? saved;
        try
        {
            saved = JsonSerializer.Deserialize(json, HoursOverridesJsonContext.Default.DictionaryStringJsonElement);
        }
        catch (JsonException)
        {
            return;
        }

        foreach (var (key, value) in saved ?? [])
        {
            if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || Read(value) is not { } correction)
            {
                continue;
            }

            _corrections.TryAdd(id, correction);
        }

        if (_corrections.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private static HoursCorrection? Read(JsonElement value)
    {
        try
        {
            return value.Deserialize(HoursOverridesJsonContext.Default.HoursCorrection) is { Rule: { } rule } correction
                   && OpeningHours.TryParse(rule, lenient: true, out var hours, out _)
                ? correction with { Rule = hours.ToRule() }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ValueTask SaveCoreAsync() =>
        preferences.SetAsync(
            StorageKey,
            JsonSerializer.Serialize(
                _corrections.ToDictionary(c => c.Key.ToString(CultureInfo.InvariantCulture), c => c.Value),
                HoursOverridesJsonContext.Default.DictionaryStringHoursCorrection));
}

/// <summary>Source-generated (trim-safe) serialization of the corrected hours.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, HoursCorrection>))]
[JsonSerializable(typeof(HoursCorrection))]
internal sealed partial class HoursOverridesJsonContext : JsonSerializerContext;
