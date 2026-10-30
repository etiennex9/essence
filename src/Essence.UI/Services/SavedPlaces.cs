using System.Text.Json;
using System.Text.Json.Serialization;

namespace Essence.UI.Services;

[JsonConverter(typeof(JsonStringEnumConverter<PlaceKind>))]
public enum PlaceKind
{
    Home,
    Work,

    /// <summary>A place the user named.</summary>
    Other,
}

/// <param name="Name">The name the user gave an <see cref="PlaceKind.Other"/> place; empty for Home and Work (the UI names them).</param>
/// <param name="Address">
/// Its address, short ("1000 Rue De La Gauchetière Ouest, Montréal"), from the address search; null until it's known (a
/// place saved while the search was down, or before it existed).
/// </param>
public sealed record SavedPlace(string Id, PlaceKind Kind, string Name, double Latitude, double Longitude, string? Address = null)
{
    public const int MaxNameLength = 40;
    public const int MaxAddressLength = 120;

    [JsonIgnore]
    public GeoPoint Point => new(Latitude, Longitude);
}

/// <summary>A place just forgotten, and where it was among the others: enough to undo it.</summary>
public sealed record RemovedPlace(SavedPlace Place, int Index);

/// <summary>
/// The places saved on this device (Home, Work, and others the user named), kept through <see cref="IPreferenceStore"/>,
/// so a trip there is one tap. There's one Home and one Work: saving another replaces it.
/// </summary>
public sealed class SavedPlaces(IPreferenceStore preferences)
{
    public const string StorageKey = "essence.places";

    /// <summary>At most this many places are kept; saving one more forgets the oldest other place.</summary>
    public const int MaxPlaces = 20;

    private readonly List<SavedPlace> _places = [];
    private Task? _loading;

    /// <summary>Raised after any change, including the saved places coming in.</summary>
    public event Action? Changed;

    /// <summary>Home, then Work, then the others in the order they were saved.</summary>
    public IReadOnlyList<SavedPlace> Places => [.. _places.OrderBy(p => p.Kind)];

    public SavedPlace? Home => _places.Find(p => p.Kind == PlaceKind.Home);

    public SavedPlace? Work => _places.Find(p => p.Kind == PlaceKind.Work);

    /// <summary>Reads the saved places once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>Saves a place: Home or Work replaces the one saved before; another is added with its name.</summary>
    public async Task<SavedPlace> SaveAsync(PlaceKind kind, GeoPoint point, string name = "", string? address = null)
    {
        await LoadAsync();
        name = kind == PlaceKind.Other ? Trim(name) : "";
        var place = new SavedPlace(NewId(), kind, name, point.Latitude, point.Longitude, TrimAddress(address));
        if (kind != PlaceKind.Other)
        {
            _places.RemoveAll(p => p.Kind == kind);
        }
        else if (_places.Count >= MaxPlaces && _places.FindIndex(p => p.Kind == PlaceKind.Other) is >= 0 and var oldest)
        {
            _places.RemoveAt(oldest);
        }

        _places.Add(place);
        await WriteAsync();
        Changed?.Invoke();
        return place;
    }

    /// <summary>The address of a place saved without one, once the address search has it.</summary>
    public async Task SetAddressAsync(string id, string address)
    {
        await LoadAsync();
        var index = _places.FindIndex(p => p.Id == id);
        if (index < 0 || TrimAddress(address) is not { } trimmed || _places[index].Address == trimmed)
        {
            return;
        }

        _places[index] = _places[index] with { Address = trimmed };
        await WriteAsync();
        Changed?.Invoke();
    }

    /// <summary>A named place's new name; Home and Work have none, and a blank name keeps the old one.</summary>
    public async Task RenameAsync(string id, string name)
    {
        await LoadAsync();
        var index = _places.FindIndex(p => p.Id == id && p.Kind == PlaceKind.Other);
        if (index < 0 || Trim(name) is not { Length: > 0 } trimmed || _places[index].Name == trimmed)
        {
            return;
        }

        _places[index] = _places[index] with { Name = trimmed };
        await WriteAsync();
        Changed?.Invoke();
    }

    /// <summary>Forgets a place; what's returned puts it back (<see cref="RestoreAsync"/>), or null when it wasn't saved.</summary>
    public async Task<RemovedPlace?> RemoveAsync(string id)
    {
        await LoadAsync();
        var index = _places.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            return null;
        }

        var removed = new RemovedPlace(_places[index], index);
        _places.RemoveAt(index);
        await WriteAsync();
        Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Undoes <see cref="RemoveAsync"/>: the place comes back where it was. A Home or Work saved since is replaced by it, as
    /// saving does, and past <see cref="MaxPlaces"/> the oldest other place goes. Nothing happens if it's still saved.
    /// </summary>
    public async Task RestoreAsync(RemovedPlace removed)
    {
        await LoadAsync();
        var place = removed.Place;
        if (_places.Exists(p => p.Id == place.Id))
        {
            return;
        }

        var index = removed.Index;
        var replaced = place.Kind != PlaceKind.Other
            ? _places.FindIndex(p => p.Kind == place.Kind)
            : _places.Count >= MaxPlaces ? _places.FindIndex(p => p.Kind == PlaceKind.Other) : -1;
        if (replaced >= 0)
        {
            _places.RemoveAt(replaced);
            index -= replaced < index ? 1 : 0;
        }

        _places.Insert(Math.Clamp(index, 0, _places.Count), place);
        await WriteAsync();
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        if (await preferences.GetAsync(StorageKey) is { Length: > 0 } json)
        {
            List<SavedPlace?>? saved;
            try
            {
                saved = JsonSerializer.Deserialize(json, SavedPlaceJsonContext.Default.ListSavedPlace);
            }
            catch (JsonException)
            {
                saved = null;
            }

            foreach (var place in saved ?? [])
            {
                if (place is { Id.Length: > 0, Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 }
                    && Enum.IsDefined(place.Kind)
                    && !_places.Exists(p => p.Id == place.Id || (place.Kind != PlaceKind.Other && p.Kind == place.Kind)))
                {
                    _places.Add(place with { Name = place.Kind == PlaceKind.Other ? Trim(place.Name) : "", Address = TrimAddress(place.Address) });
                }
            }
        }

        if (_places.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private ValueTask WriteAsync() =>
        preferences.SetAsync(StorageKey, JsonSerializer.Serialize(_places.ToList<SavedPlace?>(), SavedPlaceJsonContext.Default.ListSavedPlace));

    private static string Trim(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length > SavedPlace.MaxNameLength ? trimmed[..SavedPlace.MaxNameLength] : trimmed;
    }

    private static string? TrimAddress(string? address)
    {
        var trimmed = address?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > SavedPlace.MaxAddressLength ? trimmed[..SavedPlace.MaxAddressLength] : trimmed;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Source-generated (trim-safe) serialization of the saved places.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<SavedPlace?>))]
internal sealed partial class SavedPlaceJsonContext : JsonSerializerContext;
