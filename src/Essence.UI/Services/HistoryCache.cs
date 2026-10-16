using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>A station's price history over [<see cref="From"/>, <see cref="To"/>), every fuel.</summary>
public sealed record StationHistory(IReadOnlyList<PriceHistoryDto> Rows, DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Price histories by station and period, reused for a little while: the favorites list fetches one per station, and
/// reopening it, or opening one of its stations, shouldn't spend the API's rate limit again. Failures aren't kept.
/// </summary>
public sealed class HistoryCache(EssenceApiClient api, TimeProvider time)
{
    /// <summary>Prices change at most every 5 minutes.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(2);

    private readonly Dictionary<(long StationId, int Days), StationHistory> _entries = [];

    /// <summary>The last <paramref name="days"/> days of history, up to now.</summary>
    /// <param name="refresh">Fetches even if a fresh entry is kept: the caller knows a price changed since.</param>
    public async Task<ApiResult<StationHistory>> GetAsync(long stationId, int days, CancellationToken cancellationToken, bool refresh = false)
    {
        var now = time.GetUtcNow();
        if (!refresh && _entries.TryGetValue((stationId, days), out var cached) && now - cached.To < Freshness)
        {
            return new ApiResult<StationHistory>(cached);
        }

        var from = now.AddDays(-days);
        var result = await api.GetPriceHistoryAsync(stationId, from, now, cancellationToken);
        if (result.Value is not { } rows)
        {
            return new ApiResult<StationHistory>(null, result.Error, result.RetryAfter);
        }

        // Expired entries go when a new one comes in, so a long session of browsing stations doesn't pile them up.
        foreach (var key in _entries.Where(e => now - e.Value.To >= Freshness).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }

        var history = new StationHistory(rows, from, now);
        _entries[(stationId, days)] = history;
        return new ApiResult<StationHistory>(history);
    }
}
