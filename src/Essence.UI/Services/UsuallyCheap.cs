using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>
/// Which stations are usually among the cheapest around them, for each fuel (<c>/stations/usually-cheap</c>), for the
/// "Usually cheap" tag. A fuel's answer is asked for the first time a tag for it is wanted (<see cref="Is"/>), then again
/// <see cref="MaxAge"/> after it came: the server works out the stations once a day, but leaves out those dear at the
/// latest import, which comes about every 5 minutes. A server that hasn't it (404, an older one)
/// isn't asked again, and no tag shows. A failure keeps the last answer and is tried again <see cref="RetryAfterFailure"/>
/// later at the earliest; a 429 waits as long as it says.
/// </summary>
public sealed class UsuallyCheap : IDisposable
{
    /// <summary>A fuel's answer is asked for again after this, as the trends card's.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>After a failure, the next try waits this long, however many tags want it meanwhile.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly EssenceApiClient _api;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Fuel> _fuels = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    public UsuallyCheap(EssenceApiClient api, TimeProvider time)
    {
        _api = api;
        _time = time;
    }

    /// <summary>Raised after a fuel's answer came in, or the server said it has none.</summary>
    public event Action? Changed;

    /// <summary>The server has no answer (an older one): nothing is asked any more.</summary>
    public bool Unsupported { get; private set; }

    /// <summary>
    /// Whether the station is usually cheap for the fuel, as last answered: false until the fuel's answer is in. Asks for it
    /// when that's due, so a tag shows once it is (<see cref="Changed"/>).
    /// </summary>
    public bool Is(long stationId, string? fuel)
    {
        if (Unsupported || string.IsNullOrEmpty(fuel))
        {
            return false;
        }

        if (!_fuels.TryGetValue(fuel, out var known))
        {
            _fuels[fuel] = known = new Fuel();
        }

        if (!known.Fetching && !(_time.GetUtcNow() < known.NextAt))
        {
            _ = FetchAsync(fuel, known);
        }

        return known.Ids?.Contains(stationId) == true;
    }

    private async Task FetchAsync(string fuel, Fuel known)
    {
        known.Fetching = true;
        ApiResult<UsuallyCheapStations> result;
        try
        {
            result = await _api.GetUsuallyCheapAsync(fuel, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            known.Fetching = false;
        }

        var now = _time.GetUtcNow();
        switch (result.Error)
        {
            case ApiError.None:
                known.Ids = [.. result.Value!.Ids];
                known.NextAt = now + MaxAge;
                break;
            case ApiError.NotFound:
                Unsupported = true;
                _fuels.Clear();
                break;
            case ApiError.RateLimited:
                known.NextAt = now + (result.RetryAfter ?? RetryAfterFailure);
                return;
            default:
                known.NextAt = now + RetryAfterFailure;
                return;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed class Fuel
    {
        public HashSet<long>? Ids { get; set; }

        public bool Fetching { get; set; }

        /// <summary>Not asked before this: the answer is fresh, or the last try failed.</summary>
        public DateTimeOffset? NextAt { get; set; }
    }
}
