using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>The starred stations, kept on the device through <see cref="IPreferenceStore"/>.</summary>
public sealed class FavoriteStations(IPreferenceStore preferences)
{
    public const string StorageKey = "essence.favorites";

    private readonly List<SavedStation> _favorites = [];
    private Task? _loading;

    /// <summary>Raised after any change, including the saved favorites coming in.</summary>
    public event Action? Changed;

    /// <summary>In the order they were added.</summary>
    public IReadOnlyList<SavedStation> Favorites => _favorites;

    public int Count => _favorites.Count;

    /// <summary>Reads the saved favorites once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public bool IsFavorite(long id) => _favorites.Exists(s => s.Id == id);

    public async Task AddAsync(SavedStation station)
    {
        await LoadAsync();
        if (IsFavorite(station.Id))
        {
            return;
        }

        _favorites.Add(station);
        await SaveAsync();
        Changed?.Invoke();
    }

    public async Task RemoveAsync(long id)
    {
        await LoadAsync();
        if (_favorites.RemoveAll(s => s.Id == id) == 0)
        {
            return;
        }

        await SaveAsync();
        Changed?.Invoke();
    }

    /// <summary>Updates the saved snapshot of a favorite whose details just loaded, in case the feed changed it.</summary>
    public async Task RefreshAsync(StationDetail detail)
    {
        await LoadAsync();
        var station = SavedStation.From(detail);
        var index = _favorites.FindIndex(s => s.Id == station.Id);
        if (index < 0 || _favorites[index] == station)
        {
            return;
        }

        _favorites[index] = station;
        await SaveAsync();
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        foreach (var station in await SavedStationStorage.ReadAsync(preferences, StorageKey))
        {
            if (station is not null && !IsFavorite(station.Id))
            {
                _favorites.Add(station);
            }
        }

        if (_favorites.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private ValueTask SaveAsync() => SavedStationStorage.WriteAsync(preferences, StorageKey, _favorites);
}
