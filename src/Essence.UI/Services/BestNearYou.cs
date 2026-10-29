using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>Where Best near you ranks from: the device's location, or the map's centre when the user picks it instead.</summary>
/// <param name="AccuracyMeters">How far off the device's location may be; null for the map's centre.</param>
public sealed record BestOrigin(GeoPoint Point, bool IsMapCenter = false, double? AccuracyMeters = null)
{
    /// <summary>A location less accurate than this (a desktop's, from its network) is said to be approximate.</summary>
    public const double ApproximateBeyondMeters = 1000;

    public bool IsApproximate => !IsMapCenter && AccuracyMeters > ApproximateBeyondMeters;
}

/// <summary>
/// The latest Best near you for where the user is, the map's fuel and the car being driven, fetched again when one of them
/// changes. A scoped service, so the result outlives the pane: a station's details opening over it, or the phone chip
/// while it's closed. It refreshes by itself when the next prices should be in (<see cref="RefreshSchedule"/>, on its own
/// data's time: the map's refresh stops below zoom 9), and not while the page is hidden. It also keeps the station the
/// pane shows on top (<see cref="StationChoice"/>), so it's still chosen when the details close.
/// </summary>
public sealed class BestNearYou : IDisposable
{
    /// <summary>The same request isn't sent again within this, unless it failed.</summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    private readonly EssenceApiClient _api;
    private readonly CarProfiles _cars;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _fetchCts;
    private CancellationTokenSource? _refreshCts;
    private (BestQuery Query, DateTimeOffset At)? _lastFetch;
    private DateTimeOffset? _refreshDueAt;
    private int _lateRefreshes;
    private bool _hidden;

    public BestNearYou(EssenceApiClient api, CarProfiles cars, TimeProvider time)
    {
        _api = api;
        _cars = cars;
        _time = time;
        _cars.Changed += OnCarsChanged;
    }

    /// <summary>Raised after the result, the error or the progress changed.</summary>
    public event Action? Changed;

    public BestOrigin? Origin { get; private set; }

    /// <summary>The map's fuel.</summary>
    public string Fuel { get; private set; } = Fuels.Default;

    /// <summary>The latest answer; null before the first, and after a failure to get one for new inputs.</summary>
    public BestNearby? Result { get; private set; }

    /// <summary>What <see cref="Result"/> answers: its origin, fuel and car.</summary>
    public BestQuery? ResultQuery { get; private set; }

    public ApiError Error { get; private set; }

    /// <summary>A request is on its way for new inputs (refreshes in the background don't count).</summary>
    public bool Loading { get; private set; }

    /// <summary>When the prices behind <see cref="Result"/> were imported.</summary>
    public DateTimeOffset? DataAsOf { get; private set; }

    /// <summary>The station chosen to be shown on top, among those the pane lists; null for the pick.</summary>
    public long? SelectedId { get; private set; }

    /// <summary>The station shown on top: the one chosen, else the pick.</summary>
    public RankedStation? Shown =>
        SelectedId is { } id && Result?.Stations.FirstOrDefault(s => s.Id == id) is { } station ? station : Result?.Pick;

    /// <summary>Shows a station the pane lists on top; null, or one it doesn't list, shows the pick.</summary>
    public void Select(long? id)
    {
        var selected = Result is { } result ? StationChoice.Keep(id, true, result.Pick?.Id, StationChoice.Selectable(result)) : null;
        if (selected != SelectedId)
        {
            SelectedId = selected;
            Changed?.Invoke();
        }
    }

    /// <summary>Where to rank from; null forgets the result.</summary>
    public Task SetOriginAsync(BestOrigin? origin)
    {
        if (origin == Origin)
        {
            return Task.CompletedTask;
        }

        Origin = origin;
        return FetchAsync();
    }

    public Task SetFuelAsync(string fuel)
    {
        if (fuel == Fuel)
        {
            return Task.CompletedTask;
        }

        Fuel = fuel;
        return FetchAsync();
    }

    /// <summary>Asks again now, after a failure.</summary>
    public Task RetryAsync()
    {
        _lastFetch = null;
        return FetchAsync();
    }

    /// <summary>The page was hidden or shown: the refresh waits while it's hidden, and one overdue runs when it's back.</summary>
    public void SetVisible(bool visible)
    {
        _hidden = !visible;
        if (_hidden)
        {
            _refreshCts?.Cancel();
        }
        else
        {
            StartRefreshTimer();
        }
    }

    /// <summary>
    /// Fetches for the current inputs. A newer call cancels an older one still on its way, and the same request isn't sent
    /// again while it's on its way or fresh. A <paramref name="background"/> refresh fetches even if fresh, shows no progress,
    /// and keeps the result shown when it fails.
    /// </summary>
    private async Task FetchAsync(bool background = false)
    {
        if (Origin is not { } origin)
        {
            _fetchCts?.Cancel();
            CancelRefresh();
            _lastFetch = null;
            (Result, ResultQuery, Error, Loading, SelectedId) = (null, null, ApiError.None, false, null);
            Changed?.Invoke();
            return;
        }

        await _cars.LoadAsync();
        var car = _cars.Active;
        var query = new BestQuery(
            origin.Point.Latitude, origin.Point.Longitude, Fuel, car.FillLitres, car.LitresPer100Km, car.CostcoMember, car.MaxDetourMinutes);
        var now = _time.GetUtcNow();
        var fresh = _lastFetch is { } last && last.Query == query && (Loading || (now - last.At < Freshness && Error == ApiError.None));
        if (!background && fresh)
        {
            return;
        }

        _fetchCts?.Cancel();
        CancelRefresh();
        var cts = _fetchCts = new CancellationTokenSource();
        _lastFetch = (query, now);
        if (!background)
        {
            Loading = true;
            Changed?.Invoke();
        }

        ApiResult<BestNearby> result;
        try
        {
            result = await _api.GetBestAsync(query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (background && result.Value is null)
        {
            // What's shown stays; the next refresh tries again.
            _lateRefreshes++;
            ScheduleRefresh(result.Error == ApiError.RateLimited ? result.RetryAfter : null);
            return;
        }

        Loading = false;
        Error = result.Error;
        if (result.Value is { } best)
        {
            _lateRefreshes = background && result.DataAsOf == DataAsOf ? _lateRefreshes + 1 : 0;

            // A refresh keeps the station chosen while it's still listed; another car, fuel or location shows the pick.
            SelectedId = StationChoice.Keep(SelectedId, query == ResultQuery, best.Pick?.Id, StationChoice.Selectable(best));
            (Result, ResultQuery, DataAsOf) = (best, query, result.DataAsOf);
        }
        else
        {
            // An answer for other inputs would mislead; the refresh tries again later, as if late.
            (Result, ResultQuery, SelectedId) = (null, null, null);
            _lateRefreshes++;
        }

        ScheduleRefresh(result.Error == ApiError.RateLimited ? result.RetryAfter : null);
        Changed?.Invoke();
    }

    private void ScheduleRefresh(TimeSpan? delay = null)
    {
        var now = _time.GetUtcNow();
        _refreshDueAt = now + (delay ?? RefreshSchedule.Next(DataAsOf, now, _lateRefreshes));
        StartRefreshTimer();
    }

    private void StartRefreshTimer()
    {
        _refreshCts?.Cancel();
        if (_hidden || _refreshDueAt is not { } dueAt)
        {
            return;
        }

        var cts = _refreshCts = new CancellationTokenSource();
        _ = RefreshLaterAsync(dueAt - _time.GetUtcNow(), cts.Token);
    }

    private async Task RefreshLaterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await FetchAsync(background: true);
        }
    }

    private void CancelRefresh()
    {
        _refreshCts?.Cancel();
        _refreshDueAt = null;
    }

    /// <summary>Another car, or the car's settings: rank again.</summary>
    private void OnCarsChanged() => _ = FetchAsync();

    public void Dispose()
    {
        _cars.Changed -= OnCarsChanged;
        _fetchCts?.Cancel();
        _refreshCts?.Cancel();
    }
}
