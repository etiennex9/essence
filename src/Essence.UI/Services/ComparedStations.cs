using Essence.Contracts;

namespace Essence.UI.Services;

/// <param name="Color">The station's line in the comparison chart: an index in its palette.</param>
public sealed record ComparedStation(SavedStation Station, int Color);

/// <summary>
/// The stations in the comparison, kept on the device through <see cref="IPreferenceStore"/>. They sit in
/// <see cref="MaxStations"/> slots whose index is their color, so a station keeps its color when others are removed, and
/// across reloads.
/// </summary>
public sealed class ComparedStations(IPreferenceStore preferences)
{
    public const string StorageKey = "essence.compare";

    /// <summary>The comparison palette's size: more lines than this can't be told apart.</summary>
    public const int MaxStations = 8;

    private readonly SavedStation?[] _slots = new SavedStation?[MaxStations];
    private Task? _loading;

    /// <summary>Raised after any change, including the saved comparison coming in.</summary>
    public event Action? Changed;

    /// <summary>In slot (color) order.</summary>
    public IEnumerable<ComparedStation> Stations =>
        _slots.Select((station, color) => station is null ? null : new ComparedStation(station, color)).OfType<ComparedStation>();

    public int Count => _slots.Count(s => s is not null);

    public bool IsFull => Count == MaxStations;

    /// <summary>The chart's period, kept while the app runs.</summary>
    public int ChartDays { get; set; } = 30;

    /// <summary>Reads the saved comparison once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public bool Contains(long id) => IndexOf(id) >= 0;

    public int? ColorOf(long id) => IndexOf(id) is >= 0 and var color ? color : null;

    /// <summary>Adds a station in the first free slot. False when the comparison is full (true if it's already in).</summary>
    public async Task<bool> AddAsync(SavedStation station) => await AddAsync([station]) == 1 || Contains(station.Id);

    /// <summary>Adds the stations not compared yet, while there's room. Returns how many were added.</summary>
    public async Task<int> AddAsync(IEnumerable<SavedStation> stations)
    {
        await LoadAsync();
        var added = 0;
        foreach (var station in stations)
        {
            var free = Array.IndexOf(_slots, null);
            if (free < 0)
            {
                break;
            }

            if (!Contains(station.Id))
            {
                _slots[free] = station;
                added++;
            }
        }

        if (added > 0)
        {
            await SaveAsync();
            Changed?.Invoke();
        }

        return added;
    }

    public async Task RemoveAsync(long id)
    {
        await LoadAsync();
        var index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        _slots[index] = null;
        await SaveAsync();
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        await LoadAsync();
        if (Count == 0)
        {
            return;
        }

        Array.Clear(_slots);
        await SaveAsync();
        Changed?.Invoke();
    }

    /// <summary>Updates the saved snapshot of a compared station whose details just loaded, in case the feed changed it.</summary>
    public async Task RefreshAsync(StationDetail detail)
    {
        await LoadAsync();
        var station = SavedStation.From(detail);
        var index = IndexOf(station.Id);
        if (index < 0 || _slots[index] == station)
        {
            return;
        }

        _slots[index] = station;
        await SaveAsync();
        Changed?.Invoke();
    }

    private int IndexOf(long id) => Array.FindIndex(_slots, s => s?.Id == id);

    private async Task LoadCoreAsync()
    {
        var saved = await SavedStationStorage.ReadAsync(preferences, StorageKey);
        for (var slot = 0; slot < Math.Min(saved.Count, MaxStations); slot++)
        {
            if (saved[slot] is { } station && !Contains(station.Id))
            {
                _slots[slot] = station;
            }
        }

        if (Count > 0)
        {
            Changed?.Invoke();
        }
    }

    // Saved with the empty slots, so the colors come back as they were.
    private ValueTask SaveAsync() => SavedStationStorage.WriteAsync(preferences, StorageKey, _slots);
}
