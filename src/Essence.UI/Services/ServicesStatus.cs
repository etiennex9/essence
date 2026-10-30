using System.Globalization;
using Essence.Contracts;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.UI.Services;

/// <summary>
/// Whether trips and the address search are ready (the API's <c>/services</c>). While the map is open
/// (<see cref="Watch"/>) and one of them is being prepared, or waits to try again, it asks again every 10 seconds, so the
/// panes say how far it is and switch it on once it's ready. Nothing is asked while nothing watches, nor once both are
/// ready; a 503 or an estimate where a road distance was expected (<see cref="RefreshAsync"/>) asks again.
/// </summary>
public sealed class ServicesStatus(EssenceApiClient api, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);

    private int _watchers;
    private CancellationTokenSource? _cts;

    /// <summary>Raised when what's known changed.</summary>
    public event Action? Changed;

    /// <summary>Routing was being prepared and is ready: Best near you can be by road again, and trips can be offered.</summary>
    public event Action? RoutingReady;

    /// <summary>The address index was being prepared and is ready: what was typed can be searched again.</summary>
    public event Action? GeocodingReady;

    public ServiceReadiness? Routing { get; private set; }

    public ServiceReadiness? Geocoding { get; private set; }

    /// <summary>The routing data is being built or loaded, or that failed and is tried again later: no trips meanwhile.</summary>
    public bool RoutingPreparing => Routing is { Ready: false };

    /// <summary>The address index is being built or loaded, or that failed and is tried again later: no search meanwhile.</summary>
    public bool GeocodingPreparing => Geocoding is { Ready: false };

    /// <summary>
    /// What to say about a service that isn't ready: it will be soon (how far, when known), or it failed and tries again at
    /// a time of day; null when it's ready or doesn't say. <paramref name="prefix"/> picks the texts: <c>Trips</c> or
    /// <c>Search</c> (TripsPreparing, TripsPreparingSoon, TripsRetrying…).
    /// </summary>
    public static string? Note(ServiceReadiness? service, IStringLocalizer<Strings> l, string prefix) => service switch
    {
        { Ready: false, State: "waiting", NextAttemptAt: { } next } =>
            l[$"{prefix}Retrying", Formatting.TimeOfDay(next, TimeZoneInfo.Local, CultureInfo.CurrentCulture)].Value,
        { Ready: false, Percent: { } percent } => l[$"{prefix}Preparing", percent].Value,
        { Ready: false } => l[$"{prefix}PreparingSoon"].Value,
        _ => null,
    };

    /// <summary>The map is open: dispose the result when it closes.</summary>
    public IDisposable Watch()
    {
        _watchers++;
        if (_watchers == 1)
        {
            StartPolling();
        }

        return new Watcher(this);
    }

    /// <summary>A trip or a search answered 503: asks once now, then every 10 seconds while it's watched and not ready.</summary>
    public async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts = null;
        await FetchAsync(CancellationToken.None);
        if (_watchers > 0 && (RoutingPreparing || GeocodingPreparing))
        {
            StartPolling(delayFirst: true);
        }
    }

    private void StartPolling(bool delayFirst = false)
    {
        if (_cts is not null || _watchers == 0)
        {
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        _ = PollAsync(cts, delayFirst);
    }

    private async Task PollAsync(CancellationTokenSource cts, bool delayFirst)
    {
        try
        {
            if (delayFirst)
            {
                await Task.Delay(PollEvery, time, cts.Token);
            }

            // Asked again only while something is being prepared and the map shows it. When the API doesn't answer, asked
            // again only if something was being prepared: an API without /services isn't asked every 10 seconds.
            while (await FetchAsync(cts.Token) is not true && (RoutingPreparing || GeocodingPreparing) && _watchers > 0)
            {
                await Task.Delay(PollEvery, time, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
        }
    }

    /// <summary>Asks once: true when both are ready (or don't say), false while one is prepared, null when the API failed.</summary>
    private async Task<bool?> FetchAsync(CancellationToken cancellationToken)
    {
        var result = await api.GetServicesAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested || result.Value is not { } services)
        {
            return null;
        }

        var (routingWasPreparing, geocodingWasPreparing) = (RoutingPreparing, GeocodingPreparing);
        (Routing, Geocoding) = (services.Routing, services.Geocoding);
        Changed?.Invoke();
        if (routingWasPreparing && !RoutingPreparing)
        {
            RoutingReady?.Invoke();
        }

        if (geocodingWasPreparing && !GeocodingPreparing)
        {
            GeocodingReady?.Invoke();
        }

        return !RoutingPreparing && !GeocodingPreparing;
    }

    private void Unwatch()
    {
        if (--_watchers == 0)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    public void Dispose() => _cts?.Cancel();

    private sealed class Watcher(ServicesStatus status) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                status.Unwatch();
            }
        }
    }
}
