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
/// The trip open in Best on the way: where it goes, where it was planned from, the latest plan, and the station shown on
/// top (<see cref="StationChoice"/>). A trip is planned once, from where the user is; <see cref="RefreshAsync"/> plans it
/// again from where they are then, and another car, car setting or fuel plans it again from the same start, as does a
/// correction of a station's hours saved or undone on the device (whose stations go with each request, <c>ignoreHours</c>,
/// so the API never leaves one out for its own hours). Kept in the tab's storage (<see cref="ISessionStore"/>), so it's
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
    private readonly StationHoursOverrides? _overrides;
    private CancellationTokenSource? _planCts;

    /// <summary>What the request on its way asks; <see cref="PlanQuery"/> stays what <see cref="Plan"/> answers until it's in.</summary>
    private TripQuery? _pendingQuery;

    /// <summary>The corrected stations the plan, or the request on its way, was asked with.</summary>
    private IReadOnlyList<long> _plannedIgnoreHours = [];
    private DateTimeOffset? _unavailableAt;
    private Task? _loading;

    /// <param name="overrides">The hours corrected on the device; none when null (the tests').</param>
    public TripState(EssenceApiClient api, CarProfiles cars, ISessionStore session, TimeProvider time, StationHoursOverrides? overrides = null)
    {
        _api = api;
        _cars = cars;
        _session = session;
        _time = time;
        _overrides = overrides;
        _cars.Changed += OnCarsChanged;
        if (_overrides is not null)
        {
            _overrides.Changed += OnOverridesChanged;
        }
    }

    /// <summary>Raised after the trip, its plan, the error or the progress changed.</summary>
    public event Action? Changed;

    public TripDestination? Destination { get; private set; }

    /// <summary>Where the trip was planned from.</summary>
    public GeoPoint? Origin { get; private set; }

    /// <summary>The trip starts at the map's centre, the user's location being unknown.</summary>
    public bool FromMapCenter { get; private set; }

    /// <summary>
    /// The saved place the trip starts at, as named, when it doesn't start where the user is: the Android app's widget for
    /// a frequent route opens its own route. Null otherwise, and once planned again from where the user is.
    /// </summary>
    public string? OriginName { get; private set; }

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

    /// <summary>The station chosen to be shown on top, among those on the way; null for the pick.</summary>
    public long? SelectedId { get; private set; }

    /// <summary>The station shown on top: the one chosen, else the pick; null when there's neither (none reachable).</summary>
    public TripStation? Shown =>
        Plan is not { } plan ? null
        : SelectedId is { } id && plan.Stations.FirstOrDefault(s => s.Id == id) is { } station ? station
        : plan.Stations.FirstOrDefault(s => s.Id == plan.PickId);

    /// <summary>A trip is open.</summary>
    public bool IsOpen => Destination is not null && Origin is not null;

    /// <summary>The routing server answered 503 a moment ago: trips aren't offered until it's been a while.</summary>
    public bool Unavailable => _unavailableAt is { } at && _time.GetUtcNow() - at < UnavailableFor;

    /// <summary>The routing data is ready again (<see cref="ServicesStatus.RoutingReady"/>): trips are offered at once.</summary>
    public void ClearUnavailable()
    {
        if (_unavailableAt is not null)
        {
            _unavailableAt = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Reads the trip the tab had open, once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>Opens a trip and plans it from <paramref name="origin"/>, a saved place when <paramref name="originName"/> names it.</summary>
    public async Task StartAsync(GeoPoint origin, TripDestination destination, bool fromMapCenter = false, string? originName = null)
    {
        await LoadAsync();
        (Destination, Origin, FromMapCenter, OriginName) = (destination, origin, fromMapCenter, originName);
        (Plan, PlanQuery, PlannedAt, Error, SelectedId) = (null, null, null, ApiError.None, null);
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

        (Origin, FromMapCenter, OriginName) = (origin, fromMapCenter, null);
        await PlanAsync(force: true);
    }

    /// <summary>Asks again after a failure.</summary>
    public Task RetryAsync() => PlanAsync(force: true);

    /// <summary>Shows a station on the way on top, for as long as the trip; null, or one not on the way, shows the pick.</summary>
    public async Task SelectAsync(long? id)
    {
        var selected = Plan is { } plan ? StationChoice.Keep(id, true, plan.PickId, StationChoice.Selectable(plan)) : null;
        if (selected == SelectedId)
        {
            return;
        }

        SelectedId = selected;
        await SaveAsync();
        Changed?.Invoke();
    }

    /// <summary>Closes the trip: back to Best near you.</summary>
    public async Task EndAsync()
    {
        _planCts?.Cancel();
        (Destination, Origin, FromMapCenter, OriginName) = (null, null, false, null);
        (Plan, PlanQuery, PlannedAt, Error, Loading, _pendingQuery) = (null, null, null, ApiError.None, false, null);
        SelectedId = null;
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
        var ignoreHours = await IgnoreHoursAsync();
        var car = _cars.Active;
        var query = TripQuery.For(origin, destination.Point, Fuel, car, car.RangeKm);
        var sameIgnored = ignoreHours.SequenceEqual(_plannedIgnoreHours);
        if (!force && sameIgnored
                   && ((Loading && query == _pendingQuery) || (!Loading && query == PlanQuery && Plan is not null && Error == ApiError.None)))
        {
            return;
        }

        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        (_pendingQuery, _plannedIgnoreHours) = (query, ignoreHours);
        (Loading, Error) = (true, ApiError.None);
        Changed?.Invoke();

        ApiResult<TripPlan> result;
        try
        {
            result = await _api.GetTripAsync(query, ignoreHours, cts.Token);
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
            // Planned again for the same inputs, the station chosen stays while it's still on the way; planned from
            // elsewhere or for another car or fuel, the pick shows.
            SelectedId = StationChoice.Keep(SelectedId, query == PlanQuery, plan.PickId, StationChoice.Selectable(plan));
            (Plan, PlanQuery, PlannedAt) = (plan, query, _time.GetUtcNow());
        }
        else
        {
            // A plan for other inputs would mislead.
            (Plan, PlanQuery, PlannedAt, SelectedId) = (null, null, null, null);
        }

        await SaveAsync();
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        // A plan kept in the tab was asked with the corrections there are now, but for one changed since.
        _plannedIgnoreHours = await IgnoreHoursAsync();
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

        (Destination, Origin, FromMapCenter, OriginName) = (destination, stored.Origin, stored.FromMapCenter, stored.OriginName);
        (Plan, PlanQuery, PlannedAt) = stored.Plan is not null && stored.Query is not null
            ? (stored.Plan, stored.Query, stored.PlannedAt)
            : (null, null, null);
        SelectedId = Plan is { } plan ? StationChoice.Keep(stored.SelectedId, true, plan.PickId, StationChoice.Selectable(plan)) : null;
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
                    new StoredTrip(destination, origin, FromMapCenter, PlanQuery, Plan, PlannedAt, SelectedId, OriginName),
                    TripStateJsonContext.Default.StoredTrip))
            : ValueTask.CompletedTask;

    /// <summary>Another car, or the car's settings: plan the open trip again, from the same start.</summary>
    private void OnCarsChanged() => _ = PlanAsync();

    /// <summary>The stations whose hours the device corrected, once they're read; none without corrections.</summary>
    private async Task<IReadOnlyList<long>> IgnoreHoursAsync()
    {
        if (_overrides is null)
        {
            return [];
        }

        await _overrides.LoadAsync();
        return _overrides.StationIds;
    }

    /// <summary>
    /// A correction saved or undone: the open trip's plan, or the one on its way, is asked again from the same start with
    /// the stations it names. The corrections coming in at the start change nothing: the first plan waits for them.
    /// </summary>
    private void OnOverridesChanged()
    {
        if (IsOpen && (Plan is not null || Loading) && !_overrides!.StationIds.SequenceEqual(_plannedIgnoreHours))
        {
            _ = PlanAsync();
        }
    }

    public void Dispose()
    {
        _cars.Changed -= OnCarsChanged;
        if (_overrides is not null)
        {
            _overrides.Changed -= OnOverridesChanged;
        }

        _planCts?.Cancel();
    }

    /// <param name="SelectedId">The station shown on top; null (and in what an older version stored) the pick.</param>
    /// <param name="OriginName">The saved place the trip starts at (<see cref="TripState.OriginName"/>).</param>
    internal sealed record StoredTrip(
        TripDestination? Destination,
        GeoPoint Origin,
        bool FromMapCenter,
        TripQuery? Query,
        TripPlan? Plan,
        DateTimeOffset? PlannedAt,
        long? SelectedId = null,
        string? OriginName = null);
}

/// <summary>Source-generated (trim-safe) serialization of the trip kept in the tab's storage.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TripState.StoredTrip))]
internal sealed partial class TripStateJsonContext : JsonSerializerContext;
