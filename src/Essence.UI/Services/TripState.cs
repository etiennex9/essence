using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>Where a trip goes: a saved place, a place found by the address search, or a pin dropped on the map.</summary>
/// <param name="Kind">The saved place's kind; null for a place found or a dropped pin.</param>
/// <param name="Name">An <see cref="PlaceKind.Other"/> place's name.</param>
/// <param name="Address">The place's address, short, when it's known: the trip is named after it when it has no name.</param>
public sealed record TripDestination(double Latitude, double Longitude, PlaceKind? Kind = null, string? Name = null, string? Address = null)
{
    [JsonIgnore]
    public GeoPoint Point => new(Latitude, Longitude);

    public static TripDestination To(SavedPlace place) => new(place.Latitude, place.Longitude, place.Kind, place.Name, place.Address);
}

/// <summary>
/// The trip open in Best on the way: where it goes, where it was planned from, and the latest plan. A trip is planned once,
/// from where the user is; <see cref="RefreshAsync"/> plans it again from where they are then, and another car, car
/// setting or fuel plans it again from the same start. Kept in the tab's storage (<see cref="ISessionStore"/>), so it's
/// still there when the browser restores a tab it discarded while Google Maps navigated.
/// </summary>
public sealed class TripState : IDisposable
{
    public const string StorageKey = "essence.trip";

    /// <summary>After a 503, trips aren't offered for this long: the routing server is down or restarting.</summary>
    public static readonly TimeSpan UnavailableFor = TimeSpan.FromMinutes(2);

    private readonly EssenceApiClient _api;
    private readonly CarProfiles _cars;
    private readonly ISessionStore _session;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _planCts;

    /// <summary>What the request on its way asks; <see cref="PlanQuery"/> stays what <see cref="Plan"/> answers until it's in.</summary>
    private TripQuery? _pendingQuery;
    private DateTimeOffset? _unavailableAt;
    private Task? _loading;

    public TripState(EssenceApiClient api, CarProfiles cars, ISessionStore session, TimeProvider time)
    {
        _api = api;
        _cars = cars;
        _session = session;
        _time = time;
        _cars.Changed += OnCarsChanged;
    }

    /// <summary>Raised after the trip, its plan, the error or the progress changed.</summary>
    public event Action? Changed;

    public TripDestination? Destination { get; private set; }

    /// <summary>Where the trip was planned from.</summary>
    public GeoPoint? Origin { get; private set; }

    /// <summary>The trip starts at the map's centre, the user's location being unknown.</summary>
    public bool FromMapCenter { get; private set; }

    /// <summary>The map's fuel.</summary>
    public string Fuel { get; private set; } = Fuels.Default;

    /// <summary>The latest plan; null while the first is on its way, and after a failure to get one.</summary>
    public TripPlan? Plan { get; private set; }

    /// <summary>What <see cref="Plan"/> answers.</summary>
    public TripQuery? PlanQuery { get; private set; }

    /// <summary>When <see cref="Plan"/> was planned.</summary>
    public DateTimeOffset? PlannedAt { get; private set; }

    public ApiError Error { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>A trip is open.</summary>
    public bool IsOpen => Destination is not null && Origin is not null;

    /// <summary>The routing server answered 503 a moment ago: trips aren't offered until it's been a while.</summary>
    public bool Unavailable => _unavailableAt is { } at && _time.GetUtcNow() - at < UnavailableFor;

    /// <summary>Reads the trip the tab had open, once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>Opens a trip and plans it from <paramref name="origin"/>.</summary>
    public async Task StartAsync(GeoPoint origin, TripDestination destination, bool fromMapCenter = false)
    {
        await LoadAsync();
        (Destination, Origin, FromMapCenter) = (destination, origin, fromMapCenter);
        (Plan, PlanQuery, PlannedAt, Error) = (null, null, null, ApiError.None);
        await SaveAsync();
        await PlanAsync();
    }

    /// <summary>Plans the open trip again, from where the user is now.</summary>
    public async Task RefreshAsync(GeoPoint origin, bool fromMapCenter = false)
    {
        if (!IsOpen)
        {
            return;
        }

        (Origin, FromMapCenter) = (origin, fromMapCenter);
        await PlanAsync(force: true);
    }

    /// <summary>Asks again after a failure.</summary>
    public Task RetryAsync() => PlanAsync(force: true);

    /// <summary>Closes the trip: back to Best near you.</summary>
    public async Task EndAsync()
    {
        _planCts?.Cancel();
        (Destination, Origin, FromMapCenter) = (null, null, false);
        (Plan, PlanQuery, PlannedAt, Error, Loading, _pendingQuery) = (null, null, null, ApiError.None, false, null);
        await _session.RemoveAsync(StorageKey);
        Changed?.Invoke();
    }

    /// <summary>
    /// The map's fuel. With a trip open, it's planned again when its plan doesn't answer the fuel and the car any more:
    /// after either changed, and after a restored trip whose plan was missing or for other inputs.
    /// </summary>
    public Task SetFuelAsync(string fuel)
    {
        Fuel = fuel;
        return PlanAsync();
    }

    /// <summary>
    /// Plans the open trip for the current inputs, unless its plan already answers them (or, with
    /// <paramref name="force"/>, even then). A newer call cancels an older one still on its way.
    /// </summary>
    private async Task PlanAsync(bool force = false)
    {
        if (Destination is not { } destination || Origin is not { } origin)
        {
            return;
        }

        await _cars.LoadAsync();
        var car = _cars.Active;
        var query = new TripQuery(
            origin.Latitude, origin.Longitude, destination.Latitude, destination.Longitude, Fuel, car.FillLitres, car.LitresPer100Km,
            car.CostcoMember, car.MaxDetourMinutes, car.RangeKm, car.SpareKm);
        if (!force && ((Loading && query == _pendingQuery) || (!Loading && query == PlanQuery && Plan is not null && Error == ApiError.None)))
        {
            return;
        }

        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        _pendingQuery = query;
        (Loading, Error) = (true, ApiError.None);
        Changed?.Invoke();

        ApiResult<TripPlan> result;
        try
        {
            result = await _api.GetTripAsync(query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        (Loading, _pendingQuery) = (false, null);
        Error = result.Error;
        _unavailableAt = result.Error == ApiError.Unavailable ? _time.GetUtcNow() : null;
        if (result.Value is { } plan)
        {
            (Plan, PlanQuery, PlannedAt) = (plan, query, _time.GetUtcNow());
        }
        else
        {
            // A plan for other inputs would mislead.
            (Plan, PlanQuery, PlannedAt) = (null, null, null);
        }

        await SaveAsync();
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        StoredTrip? stored;
        try
        {
            stored = await _session.GetAsync(StorageKey) is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, TripStateJsonContext.Default.StoredTrip)
                : null;
        }
        catch (JsonException)
        {
            stored = null;
        }

        if (stored is not { Destination: { } destination })
        {
            return;
        }

        (Destination, Origin, FromMapCenter) = (destination, stored.Origin, stored.FromMapCenter);
        (Plan, PlanQuery, PlannedAt) = stored.Plan is not null && stored.Query is not null
            ? (stored.Plan, stored.Query, stored.PlannedAt)
            : (null, null, null);
        if (stored.Query?.Fuel is { } fuel && Plan is not null)
        {
            Fuel = fuel;
        }

        Changed?.Invoke();
    }

    private ValueTask SaveAsync() =>
        Destination is { } destination && Origin is { } origin
            ? _session.SetAsync(
                StorageKey,
                JsonSerializer.Serialize(
                    new StoredTrip(destination, origin, FromMapCenter, PlanQuery, Plan, PlannedAt),
                    TripStateJsonContext.Default.StoredTrip))
            : ValueTask.CompletedTask;

    /// <summary>Another car, or the car's settings: plan the open trip again, from the same start.</summary>
    private void OnCarsChanged() => _ = PlanAsync();

    public void Dispose()
    {
        _cars.Changed -= OnCarsChanged;
        _planCts?.Cancel();
    }

    internal sealed record StoredTrip(
        TripDestination? Destination,
        GeoPoint Origin,
        bool FromMapCenter,
        TripQuery? Query,
        TripPlan? Plan,
        DateTimeOffset? PlannedAt);
}

/// <summary>Source-generated (trim-safe) serialization of the trip kept in the tab's storage.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TripState.StoredTrip))]
internal sealed partial class TripStateJsonContext : JsonSerializerContext;
