using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>What <see cref="EssenceApiClient.GetTrendsAsync"/> asks for: where, which fuel, and whether Costco counts.</summary>
public sealed record TrendsQuery(double Latitude, double Longitude, string Fuel, bool CostcoMember)
{
    /// <summary>The trends of the area Best near you ranked.</summary>
    public static TrendsQuery For(BestQuery query) => new(query.Latitude, query.Longitude, query.Fuel, query.CostcoMember);

    /// <summary>The same fuel and Costco, and within <see cref="AreaTrends.SameAreaKm"/>: the same area, near enough.</summary>
    public bool IsNear(TrendsQuery other) =>
        Fuel == other.Fuel
        && CostcoMember == other.CostcoMember
        && Geo.DistanceKm(new GeoPoint(Latitude, Longitude), new GeoPoint(other.Latitude, other.Longitude)) <= AreaTrends.SameAreaKm;
}

/// <summary>
/// How prices move around where Best near you ranks from (<c>/stations/trends</c>), for its card under the tiles. Only
/// while the pane shows Best near you (<see cref="Attach"/>): it asks once when attached, then again when Best's answer
/// is for a point more than <see cref="SameAreaKm"/> away, another fuel or Costco, or newer prices, or
/// <see cref="MaxAge"/> after its last answer, so a rise that's over goes away. It follows Best near you's refreshes, so
/// not while the page is hidden. A server too old to have the trends (404) isn't asked again: the card stays hidden. A
/// failure keeps the last answer; a 429 waits as long as it says.
/// </summary>
public sealed class AreaTrends : IDisposable
{
    /// <summary>Points this close share an answer: GPS wobbles, and the area is 10 km wide.</summary>
    public const double SameAreaKm = 1;

    /// <summary>An answer is asked for again after this, even with the same prices.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly EssenceApiClient _api;
    private readonly BestNearYou _best;
    private readonly TimeProvider _time;
    private int _attached;
    private CancellationTokenSource? _fetchCts;
    private (TrendsQuery Query, DateTimeOffset? DataAsOf, DateTimeOffset At)? _asked;
    private DateTimeOffset? _waitUntil;

    public AreaTrends(EssenceApiClient api, BestNearYou best, TimeProvider time)
    {
        _api = api;
        _best = best;
        _time = time;
    }

    /// <summary>Raised after an answer came in, or the server said it has no trends.</summary>
    public event Action? Changed;

    /// <summary>The latest answer; null before the first.</summary>
    public PriceTrends? Result { get; private set; }

    /// <summary>What <see cref="Result"/> answers.</summary>
    public TrendsQuery? ResultQuery { get; private set; }

    /// <summary>The server has no trends (an older one): nothing is asked any more.</summary>
    public bool Unsupported { get; private set; }

    /// <summary>The pane shows Best near you: follow its answers. Each call needs its <see cref="Detach"/>.</summary>
    public void Attach()
    {
        if (_attached++ == 0)
        {
            _best.Changed += OnBestChanged;
            _ = RefreshAsync();
        }
    }

    public void Detach()
    {
        if (_attached > 0 && --_attached == 0)
        {
            _best.Changed -= OnBestChanged;
            _fetchCts?.Cancel();
        }
    }

    /// <summary>The answer for Best near you's <paramref name="query"/>: the latest when it's for the same area, else null.</summary>
    public PriceTrends? For(BestQuery? query) =>
        query is not null && ResultQuery is { } answered && answered.IsNear(TrendsQuery.For(query)) ? Result : null;

    private void OnBestChanged() => _ = RefreshAsync();

    /// <summary>Asks for Best near you's area when that's due (see the class); one request at a time.</summary>
    public async Task RefreshAsync()
    {
        if (Unsupported || _attached == 0 || _best.ResultQuery is not { } bestQuery)
        {
            return;
        }

        var query = TrendsQuery.For(bestQuery);
        var now = _time.GetUtcNow();
        if (_waitUntil > now
            || (_asked is { } asked && asked.Query.IsNear(query) && asked.DataAsOf == _best.DataAsOf && now - asked.At < MaxAge))
        {
            return;
        }

        _fetchCts?.Cancel();
        var cts = _fetchCts = new CancellationTokenSource();
        _asked = (query, _best.DataAsOf, now);
        ApiResult<PriceTrends> result;
        try
        {
            result = await _api.GetTrendsAsync(query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        switch (result.Error)
        {
            case ApiError.None:
                (Result, ResultQuery) = (result.Value, query);
                break;
            case ApiError.NotFound:
                (Unsupported, Result, ResultQuery) = (true, null, null);
                break;
            case ApiError.RateLimited:
                _waitUntil = _time.GetUtcNow() + (result.RetryAfter ?? MaxAge);
                _asked = null;
                return;
            default:
                // Asked again at Best near you's next answer.
                _asked = null;
                return;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        _best.Changed -= OnBestChanged;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
    }
}
