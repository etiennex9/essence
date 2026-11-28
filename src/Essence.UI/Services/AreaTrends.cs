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
/// not while the page is hidden. Before the first, it asks the server whether it has the trends without saying where
/// (<see cref="EssenceApiClient.ProbeTrendsAsync"/>): an older server's web container would log the point. A server
/// that hasn't (404) isn't asked again, and the card stays hidden. A failure keeps the last answer and is tried again
/// <see cref="RetryAfterFailure"/> later at the earliest; a 429 waits as long as it says. An answer older than
/// <see cref="ShownFor"/> isn't shown, so the card never contradicts tiles much newer than it.
/// </summary>
public sealed class AreaTrends : IDisposable
{
    /// <summary>Points this close share an answer: GPS wobbles, and the area is 10 km wide.</summary>
    public const double SameAreaKm = 1;

    /// <summary>An answer is asked for again after this, even with the same prices.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>An answer is shown this long at most: past it, the card waits for a new one.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromMinutes(30);

    /// <summary>After a failure, the next try waits this long, whatever Best near you does meanwhile (a tap on a tile).</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly EssenceApiClient _api;
    private readonly BestNearYou _best;
    private readonly TimeProvider _time;
    private int _attached;
    private CancellationTokenSource? _fetchCts;
    private bool _fetching;
    private (TrendsQuery Query, DateTimeOffset? DataAsOf, DateTimeOffset At)? _asked;
    private DateTimeOffset? _answeredAt;
    private DateTimeOffset? _waitUntil;

    /// <summary>Whether the server has the trends: unknown until it's asked once.</summary>
    private bool? _supported;

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

    /// <summary>The pane closed or went to a trip: stop following, and drop a request on its way (asked again when attached).</summary>
    public void Detach()
    {
        if (_attached > 0 && --_attached == 0)
        {
            _best.Changed -= OnBestChanged;
            if (_fetching)
            {
                _asked = null;
            }

            _fetchCts?.Cancel();
        }
    }

    /// <summary>
    /// The answer for Best near you's <paramref name="query"/>: the latest when it's for the same area and no older than
    /// <see cref="ShownFor"/>, else null.
    /// </summary>
    public PriceTrends? For(BestQuery? query) =>
        query is not null
        && ResultQuery is { } answered
        && answered.IsNear(TrendsQuery.For(query))
        && _answeredAt is { } at
        && _time.GetUtcNow() - at <= ShownFor
            ? Result
            : null;

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
        _fetching = true;
        ApiResult<PriceTrends> result;
        try
        {
            if (_supported is null)
            {
                var probe = await _api.ProbeTrendsAsync(cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (probe == ApiError.NotFound)
                {
                    (_supported, Unsupported, Result, ResultQuery) = (false, true, null, null);
                    Changed?.Invoke();
                    return;
                }

                if (probe is not (ApiError.None or ApiError.Invalid))
                {
                    Failed();
                    return;
                }

                _supported = true;
            }

            result = await _api.GetTrendsAsync(query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_fetchCts, cts))
            {
                _fetching = false;
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        switch (result.Error)
        {
            case ApiError.None:
                (Result, ResultQuery, _answeredAt) = (result.Value, query, _time.GetUtcNow());
                break;
            case ApiError.NotFound:
                (_supported, Unsupported, Result, ResultQuery) = (false, true, null, null);
                break;
            case ApiError.RateLimited:
                _waitUntil = _time.GetUtcNow() + (result.RetryAfter ?? MaxAge);
                _asked = null;
                return;
            default:
                Failed();
                return;
        }

        Changed?.Invoke();
    }

    /// <summary>A request failed: the last answer stays, and the next try waits <see cref="RetryAfterFailure"/>.</summary>
    private void Failed()
    {
        _asked = null;
        _waitUntil = _time.GetUtcNow() + RetryAfterFailure;
    }

    public void Dispose()
    {
        _best.Changed -= OnBestChanged;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
    }
}
