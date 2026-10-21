using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>
/// Notices when a release other than the one this tab runs is deployed, by reading release.json again (README → Versions).
/// While the page is in use, it only says so (<see cref="Available"/>, for the user to reload). It asks for the reload
/// itself (<see cref="ReloadDue"/>) while the page is away, out of sight for <see cref="AwayAfter"/> or more, or just as it
/// comes back from being away, and only right after a read that found the other release: one known from earlier may have
/// been rolled back since, or the network may be gone. Releases are told apart by their commit, since a day's version can
/// be deployed more than once, and any other commit counts, so a rollback reaches open tabs too. The release this tab runs
/// is the first one release.json gives; a build without release notes (a development version) or without a commit is
/// never checked.
/// </summary>
public sealed class UpdateCheck(ReleaseNotes notes, IReleaseNotesSource source, TimeProvider time)
{
    /// <summary>Between checks, whether the page is in sight or not: a release found while it's away reloads out of sight.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Coming back checks unless the last check is this recent, so switching tabs back and forth doesn't. Coming back from
    /// being away with the offer up always checks, as that answer may reload.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    /// <summary>Out of sight this long, the page is away: a quick switch to another tab and back keeps it as it is.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(1);

    /// <summary>After coming back from being away, how long a check's answer may still reload, before the user is busy again.</summary>
    public static readonly TimeSpan ReloadWindow = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _life;
    private CancellationTokenSource? _awayCts;
    private Task? _check;
    private DateTimeOffset? _lastCheck;

    /// <summary>The commit this tab runs; null until release.json is read.</summary>
    private string? _running;

    /// <summary>The commit release.json gave last.</summary>
    private string? _latest;

    /// <summary>No release notes, or no commit: nothing to compare.</summary>
    private bool _off;

    /// <summary>When the page went out of sight; null while it's in sight.</summary>
    private DateTimeOffset? _hiddenAt;

    /// <summary>When the page came back from being away; null if it wasn't away before it was last shown.</summary>
    private DateTimeOffset? _backAt;

    /// <summary>Another release is deployed: the page offers to reload.</summary>
    public bool Available => _running is not null && _latest is not null && _latest != _running;

    /// <summary><see cref="Available"/> changed.</summary>
    public event Action? Changed;

    /// <summary>The page should reload now, as it's away or just back.</summary>
    public event Action? ReloadDue;

    /// <summary>Starts checking, unless it's started already.</summary>
    public void Start()
    {
        if (_life is not null || _off)
        {
            return;
        }

        var life = _life = new CancellationTokenSource();
        _ = RunAsync(life.Token);
        StartAwayTimer();
    }

    /// <summary>Stops checking; a check on its way is dropped. <see cref="Start"/> starts again.</summary>
    public void Stop()
    {
        _life?.Cancel();
        _life = null;
        StopAwayTimer();
        _check = null;
    }

    /// <summary>
    /// The page came into sight or went out of it. Coming back checks, unless the last check is recent and the page wasn't
    /// away with the offer up.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (visible == _hiddenAt is null)
        {
            // Reported twice.
            return;
        }

        var now = time.GetUtcNow();
        StopAwayTimer();
        if (!visible)
        {
            _hiddenAt = now;
            StartAwayTimer();
            return;
        }

        // Timers may have been frozen while away (phones), so the time away is taken from the clock.
        _backAt = now - _hiddenAt >= AwayAfter ? now : null;
        _hiddenAt = null;
        if (_life is { } life && ((_backAt is not null && Available) || _lastCheck is not { } last || now - last >= MinInterval))
        {
            _ = CheckAsync(life.Token);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_running is null)
            {
                await CheckAsync(cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, time, cancellationToken);
                await CheckAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>Waits until the page is away, then reads release.json again if another release was found in the meantime.</summary>
    private void StartAwayTimer()
    {
        if (_life is not { } life || _hiddenAt is not { } hiddenAt)
        {
            return;
        }

        var cts = _awayCts = new CancellationTokenSource();
        _ = AwayAsync(hiddenAt + AwayAfter - time.GetUtcNow(), cts.Token);

        async Task AwayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Available)
            {
                await CheckAsync(life.Token);
            }
        }
    }

    private void StopAwayTimer()
    {
        _awayCts?.Cancel();
        _awayCts?.Dispose();
        _awayCts = null;
    }

    /// <summary>One read at a time: a check asked for while one is on its way waits for that one.</summary>
    private Task CheckAsync(CancellationToken cancellationToken)
    {
        if (_check is not { IsCompleted: false })
        {
            _check = Check();
        }

        return _check;

        async Task Check()
        {
            _lastCheck = time.GetUtcNow();
            if (_running is null)
            {
                // The release this tab runs: read at startup by the page, or again here if that read failed. Not compared.
                await notes.LoadReleaseAsync();
                if (!cancellationToken.IsCancellationRequested)
                {
                    Adopt();
                }

                return;
            }

            ApiResult<ReleaseInfo> result;
            try
            {
                result = await source.GetReleaseAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // A failure, or a 404 while the web app restarts, says nothing about the release: the next check asks again.
            if (cancellationToken.IsCancellationRequested || result.Value?.Commit is not { } commit)
            {
                return;
            }

            var wasAvailable = Available;
            _latest = commit;
            if (Available != wasAvailable)
            {
                Changed?.Invoke();
            }

            if (Available && ReloadsNow(time.GetUtcNow()))
            {
                ReloadDue?.Invoke();
            }
        }
    }

    private void Adopt()
    {
        switch (notes.State)
        {
            case ReleaseState.Loaded when notes.Release?.Commit is { } commit:
                _running = commit;
                break;
            case ReleaseState.Loaded or ReleaseState.Missing:
                _off = true;
                Stop();
                break;
            default:
                // Failed: the next check tries again.
                break;
        }
    }

    private bool ReloadsNow(DateTimeOffset now) => _hiddenAt is { } hiddenAt
        ? now - hiddenAt >= AwayAfter
        : _backAt is { } backAt && now - backAt < ReloadWindow;
}
