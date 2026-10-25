using System.Text.Json;
using System.Text.Json.Serialization;

namespace Essence.UI.Services;

/// <summary>A car that savings are worked out for. Each one has its own settings.</summary>
/// <param name="Name">Empty for the typical car shown until one is set up (the UI names it).</param>
/// <param name="Fuel">Kept the same as the map's: picking the car switches the map to it, and the map's fuel updates it.</param>
/// <param name="FillLitres">What the driver buys at a stop.</param>
/// <param name="CostcoMember">Costco sells to members only: without, it's left out.</param>
/// <param name="SpareKm">On trips, the range never counted on.</param>
/// <param name="MaxDetourMinutes">The longest detour: how much longer than the closest's a station's drive may take to be picked.</param>
public sealed record CarProfile(
    string Id,
    string Name,
    string Fuel,
    decimal FillLitres,
    decimal LitresPer100Km,
    bool CostcoMember,
    int SpareKm,
    int MaxDetourMinutes)
{
    public const decimal MinFillLitres = 1;
    public const decimal MaxFillLitres = 200;
    public const decimal MinLitresPer100Km = 1;
    public const decimal MaxLitresPer100Km = 40;
    public const int MaxSpareKm = 200;
    public const int MaxDetourLimit = 120;

    /// <summary>A typical car: 40 L at 8.5 L/100 km, not a Costco member, 30 km spare, 10 min longest detour.</summary>
    public static CarProfile Typical(string id, string name, string fuel) => new(id, name, Fuels.Normalize(fuel), 40, 8.5m, false, 30, 10);

    /// <summary>The same car with every setting in range: a known fuel, and numbers clamped to what the API accepts.</summary>
    public CarProfile Normalized() => this with
    {
        Name = Name?.Trim() ?? "",
        Fuel = Fuels.Normalize(Fuel),
        FillLitres = Math.Clamp(FillLitres, MinFillLitres, MaxFillLitres),
        LitresPer100Km = Math.Clamp(LitresPer100Km, MinLitresPer100Km, MaxLitresPer100Km),
        SpareKm = Math.Clamp(SpareKm, 0, MaxSpareKm),
        MaxDetourMinutes = Math.Clamp(MaxDetourMinutes, 0, MaxDetourLimit),
    };
}

/// <summary>
/// The cars set up on this device and the one being driven, kept through <see cref="IPreferenceStore"/>: no accounts.
/// Until one is set up, <see cref="Active"/> is a typical car on the map's fuel.
/// </summary>
public sealed class CarProfiles(IPreferenceStore preferences)
{
    public const string StorageKey = "essence.cars";
    public const string ActiveKey = "essence.car";

    /// <summary>The id of the typical car, before any is set up.</summary>
    public const string TypicalId = "typical";

    private readonly List<CarProfile> _cars = [];
    private string? _activeId;
    private string _fuel = Fuels.Default;
    private Task? _loading;

    /// <summary>Raised after any change to the cars or the one being driven, including the saved ones coming in.</summary>
    public event Action? Changed;

    /// <summary>The cars set up, in the order they were added; empty until one is.</summary>
    public IReadOnlyList<CarProfile> Cars => _cars;

    /// <summary>No car is set up: <see cref="Active"/> is a typical one.</summary>
    public bool IsTypical => _cars.Count == 0;

    /// <summary>The car being driven.</summary>
    public CarProfile Active => _cars.Find(c => c.Id == _activeId) ?? (_cars.Count > 0 ? _cars[0] : CarProfile.Typical(TypicalId, "", _fuel));

    /// <summary>Reads the saved cars once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>The map's fuel changed: the car being driven takes it.</summary>
    public async Task SetFuelAsync(string fuel)
    {
        await LoadAsync();
        fuel = Fuels.Normalize(fuel);
        if (!IsTypical)
        {
            _fuel = fuel;
            await SaveAsync(Active with { Fuel = fuel });
        }
        else if (fuel != _fuel)
        {
            _fuel = fuel;
            Changed?.Invoke();
        }
    }

    /// <summary>Switches to another car set up.</summary>
    public async Task SelectAsync(string id)
    {
        await LoadAsync();
        if (Active.Id == id || !_cars.Exists(c => c.Id == id))
        {
            return;
        }

        _activeId = id;
        await preferences.SetAsync(ActiveKey, id);
        Changed?.Invoke();
    }

    /// <summary>Saves a car's settings. Saving the typical car sets it up as the first car.</summary>
    public async Task SaveAsync(CarProfile car)
    {
        await LoadAsync();
        car = car.Normalized();
        var index = _cars.FindIndex(c => c.Id == car.Id);
        if (index >= 0 && _cars[index] == car)
        {
            return;
        }

        if (index >= 0)
        {
            _cars[index] = car;
        }
        else
        {
            if (car.Id == TypicalId)
            {
                car = car with { Id = NewId() };
            }

            _cars.Add(car);
            _activeId = car.Id;
            await preferences.SetAsync(ActiveKey, car.Id);
        }

        await WriteAsync();
        Changed?.Invoke();
    }

    /// <summary>
    /// Sets up another car, typical, on the map's fuel, and drives it. With no car set up yet, the typical one shown so far
    /// is set up first, as <paramref name="firstName"/>, so it isn't lost.
    /// </summary>
    public async Task<CarProfile> AddAsync(string name, string firstName)
    {
        await LoadAsync();
        if (IsTypical)
        {
            _cars.Add(Active with { Id = NewId(), Name = firstName });
        }

        var car = CarProfile.Typical(NewId(), name, _fuel).Normalized();
        _cars.Add(car);
        _activeId = car.Id;
        await preferences.SetAsync(ActiveKey, car.Id);
        await WriteAsync();
        Changed?.Invoke();
        return car;
    }

    /// <summary>Deletes a car; the first one left is driven next, or the typical car once none is left.</summary>
    public async Task RemoveAsync(string id)
    {
        await LoadAsync();
        var wasActive = Active.Id == id;
        if (_cars.RemoveAll(c => c.Id == id) == 0)
        {
            return;
        }

        if (wasActive)
        {
            _activeId = _cars.FirstOrDefault()?.Id;
            await preferences.SetAsync(ActiveKey, _activeId ?? "");
        }

        await WriteAsync();
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        if (await preferences.GetAsync(StorageKey) is { Length: > 0 } json)
        {
            List<CarProfile?>? saved;
            try
            {
                saved = JsonSerializer.Deserialize(json, CarProfileJsonContext.Default.ListCarProfile);
            }
            catch (JsonException)
            {
                saved = null;
            }

            foreach (var car in saved ?? [])
            {
                if (car is { Id.Length: > 0 } && car.Id != TypicalId && !_cars.Exists(c => c.Id == car.Id))
                {
                    _cars.Add(car.Normalized());
                }
            }
        }

        _activeId = await preferences.GetAsync(ActiveKey);
        if (_cars.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private ValueTask WriteAsync() =>
        preferences.SetAsync(StorageKey, JsonSerializer.Serialize(_cars.ToList<CarProfile?>(), CarProfileJsonContext.Default.ListCarProfile));

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Source-generated (trim-safe) serialization of the saved cars.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<CarProfile?>))]
internal sealed partial class CarProfileJsonContext : JsonSerializerContext;
