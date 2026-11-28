using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Npgsql;

namespace Essence.Api;

/// <summary>What a request says about who sent it (<see cref="Usage.Classify"/>): an Android installation, or a browser on the web app.</summary>
public abstract record UsageSighting(DateOnly Day);

/// <param name="Install">The installation's <see cref="Usage.InstallKey"/>.</param>
public sealed record AndroidSighting(DateOnly Day, Guid Install, long VersionCode, string Version) : UsageSighting(Day);

/// <summary>Kept in memory only until <see cref="UsageCounter"/> hashes it, at most <see cref="UsageCounter.FlushInterval"/>.</summary>
public sealed record WebSighting(DateOnly Day, IPAddress Address, string UserAgent, string Os) : UsageSighting(Day);

/// <summary>A row of <c>usage_web</c>.</summary>
public sealed record WebVisit(DateOnly Day, Guid Visitor, string Os);

/// <summary>Where <see cref="UsageCounter"/> keeps its counts: <see cref="PostgresUsageStore"/>, or a test's.</summary>
public interface IUsageStore
{
    /// <summary>
    /// The secret of <paramref name="day"/>'s visitor hashes, made now if it's <paramref name="today"/> and there's none
    /// yet; null for an earlier day without one (it was deleted). Deletes the secrets of the days before today.
    /// </summary>
    Task<byte[]?> SaltAsync(DateOnly day, DateOnly today, CancellationToken cancellationToken);

    /// <summary>Adds the rows; those already there stay as they are.</summary>
    Task WriteAsync(IReadOnlyCollection<AndroidSighting> android, IReadOnlyCollection<WebVisit> web, CancellationToken cancellationToken);
}

/// <summary>
/// Counts who uses the apps, from the requests the API gets (README → Usage): each Android installation by the id it sends
/// (<c>Essence-Install</c>) and the build its User-Agent names, each browser on the web app by a hash of its address and
/// User-Agent that changes every day. No location, no address and no path is kept, and nothing is counted for a request
/// that isn't the apps'.
/// </summary>
public static partial class Usage
{
    public const string InstallHeader = "Essence-Install";

    /// <summary>What a browser's User-Agent says beyond this isn't needed to tell browsers apart.</summary>
    private const int MaxUserAgentLength = 512;

    /// <summary>
    /// What the API counts of a request on <paramref name="day"/> (Québec's), or null: the Android app's calls, which name
    /// their build (<c>Essence-Android/2026.11.18.2 (463007)</c>) and carry the installation's id (builds from before
    /// 2026-11-22 don't, and aren't counted); and a browser's calls from a page's script (<c>Sec-Fetch-Mode</c> <c>cors</c>
    /// or <c>same-origin</c>), which monitors, health checks, crawlers and opening an API address in a tab don't send.
    /// </summary>
    public static UsageSighting? Classify(HttpRequest request, IPAddress? address, DateOnly day)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        if (userAgent.StartsWith("Essence-Android/", StringComparison.Ordinal))
        {
            return AndroidAgent().Match(userAgent) is { Success: true } match
                   && long.TryParse(match.Groups["code"].ValueSpan, out var code)
                   && Guid.TryParse(request.Headers[InstallHeader].ToString(), out var install)
                ? new AndroidSighting(day, InstallKey(install), code, match.Groups["name"].Value)
                : null;
        }

        var mode = request.Headers["Sec-Fetch-Mode"].ToString();
        if (address is null
            || mode is not ("cors" or "same-origin")
            || !userAgent.Contains("Mozilla/", StringComparison.Ordinal)
            || NotAPerson().IsMatch(userAgent))
        {
            return null;
        }

        var agent = userAgent.Length > MaxUserAgentLength ? userAgent[..MaxUserAgentLength] : userAgent;
        return new WebSighting(day, address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, agent, OsOf(agent));
    }

    /// <summary>
    /// The operating system a browser's User-Agent names. An iPad asking for desktop sites says it's a Mac, so it counts as
    /// <c>macos</c>.
    /// </summary>
    public static string OsOf(string userAgent) =>
        userAgent.Contains("Android", StringComparison.Ordinal) ? "android"
        : userAgent.Contains("iPhone", StringComparison.Ordinal) || userAgent.Contains("iPad", StringComparison.Ordinal)
          || userAgent.Contains("iPod", StringComparison.Ordinal) ? "ios"
        : userAgent.Contains("CrOS", StringComparison.Ordinal) ? "chromeos"
        : userAgent.Contains("Windows", StringComparison.Ordinal) ? "windows"
        : userAgent.Contains("Macintosh", StringComparison.Ordinal) ? "macos"
        : userAgent.Contains("Linux", StringComparison.Ordinal) || userAgent.Contains("X11", StringComparison.Ordinal) ? "linux"
        : "other";

    /// <summary>What <c>usage_android</c> keeps of an installation's id: a hash, so that the database never holds the id the phone has.</summary>
    public static Guid InstallKey(Guid install) =>
        FirstHalf(SHA256.HashData(Encoding.UTF8.GetBytes("essence-install:" + install.ToString("N"))));

    /// <summary>A browser's visitor on the day whose secret is <paramref name="salt"/>.</summary>
    public static Guid VisitorKey(byte[] salt, IPAddress address, string userAgent) =>
        FirstHalf(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(address + "\n" + userAgent)));

    private static Guid FirstHalf(byte[] hash) => new(hash.AsSpan(0, 16));

    /// <summary>Adds the counting, when the usage role's connection string is set.</summary>
    public static IServiceCollection AddUsageCounting(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => new PostgresUsageStore(NpgsqlDataSource.Create(connectionString)));
        services.AddSingleton<IUsageStore>(sp => sp.GetRequiredService<PostgresUsageStore>());
        services.AddSingleton<UsageCounter>();
        services.AddHostedService(sp => sp.GetRequiredService<UsageCounter>());
        return services;
    }

    /// <summary>Counts each request, before anything answers it; nothing without <see cref="AddUsageCounting"/>.</summary>
    public static IApplicationBuilder UseUsageCounting(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<UsageCounter>() is { } counter)
        {
            app.Use((context, next) =>
            {
                counter.Observe(context);
                return next(context);
            });
        }

        return app;
    }

    [GeneratedRegex(@"^Essence-Android/(?<name>[0-9A-Za-z.+\-]{1,40}) \((?<code>[0-9]{1,18})\)$")]
    private static partial Regex AndroidAgent();

    [GeneratedRegex("bot|crawl|spider|slurp|headless|lighthouse", RegexOptions.IgnoreCase)]
    private static partial Regex NotAPerson();
}

/// <summary>
/// Takes each request's <see cref="UsageSighting"/> (<see cref="Observe"/>, which never waits) and writes the new ones every
/// <see cref="FlushInterval"/>, and once more as the API stops. A row already written today isn't written again. When the
/// database can't be reached, the rows wait for the next try, at most <see cref="MaxPending"/> of them; counting is never
/// worth failing a request for.
/// </summary>
public sealed class UsageCounter(IUsageStore store, TimeProvider time, ILogger<UsageCounter> logger) : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    public const int MaxPending = 20_000;

    private static readonly TimeSpan LastFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<UsageSighting> _sightings = Channel.CreateBounded<UsageSighting>(
        new BoundedChannelOptions(MaxPending) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly HashSet<AndroidSighting> _pendingAndroid = [];
    private readonly HashSet<WebSighting> _pendingWeb = [];
    private readonly HashSet<object> _written = [];
    private readonly Dictionary<DateOnly, byte[]> _salts = [];
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private DateOnly _writtenDay;
    private bool _failing;

    public void Observe(HttpContext context)
    {
        if (Usage.Classify(context.Request, context.Connection.RemoteIpAddress, Today()) is { } sighting)
        {
            _sightings.Writer.TryWrite(sighting);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping: what's left is written below.
        }

        using var last = new CancellationTokenSource(LastFlushTimeout);
        await FlushAsync(last.Token);
    }

    /// <summary>Writes what came in since the last time. Never throws.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _flushing.WaitAsync(CancellationToken.None);
        try
        {
            var today = Today();
            if (today != _writtenDay)
            {
                _written.Clear();
                _writtenDay = today;
                foreach (var day in _salts.Keys.Where(d => d < today.AddDays(-1)).ToList())
                {
                    _salts.Remove(day);
                }
            }

            while (_sightings.Reader.TryRead(out var sighting))
            {
                if (_pendingAndroid.Count + _pendingWeb.Count >= MaxPending)
                {
                    break;
                }

                switch (sighting)
                {
                    case AndroidSighting install when !_written.Contains(install):
                        _pendingAndroid.Add(install);
                        break;
                    case WebSighting browser:
                        _pendingWeb.Add(browser);
                        break;
                }
            }

            if (_pendingAndroid.Count == 0 && _pendingWeb.Count == 0)
            {
                return;
            }

            var visits = new List<WebVisit>();
            foreach (var group in _pendingWeb.GroupBy(w => w.Day))
            {
                if (await SaltAsync(group.Key, today, cancellationToken) is not { } salt)
                {
                    // A day whose secret is gone: too late to count.
                    continue;
                }

                visits.AddRange(group
                    .Select(w => new WebVisit(w.Day, Usage.VisitorKey(salt, w.Address, w.UserAgent), w.Os))
                    .Where(v => !_written.Contains(v)));
            }

            var android = _pendingAndroid.ToList();
            visits = [.. visits.Distinct()];
            if (android.Count > 0 || visits.Count > 0)
            {
                await store.WriteAsync(android, visits, cancellationToken);
            }

            foreach (var row in android.Where(a => a.Day == today))
            {
                _written.Add(row);
            }

            foreach (var row in visits.Where(v => v.Day == today))
            {
                _written.Add(row);
            }

            _pendingAndroid.Clear();
            _pendingWeb.Clear();
            if (_failing)
            {
                _failing = false;
                logger.LogInformation("The usage counts are written again.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping, or the last try ran out of time: kept for the next one, if any.
        }
        catch (Exception ex)
        {
            // Whatever went wrong: a hosted service that throws stops the API. Kept for the next try, at most MaxPending.
            if (!_failing)
            {
                _failing = true;
                logger.LogWarning(ex, "Couldn't write the usage counts; trying again in {Interval}.", FlushInterval);
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    private async Task<byte[]?> SaltAsync(DateOnly day, DateOnly today, CancellationToken cancellationToken)
    {
        if (_salts.TryGetValue(day, out var salt))
        {
            return salt;
        }

        salt = await store.SaltAsync(day, today, cancellationToken);
        if (salt is not null)
        {
            _salts[day] = salt;
        }

        return salt;
    }

    private DateOnly Today() => PositionReview.Today(time.GetUtcNow());
}

/// <summary><see cref="IUsageStore"/> in the <c>usage_*</c> tables, as the <c>essence_usage</c> role.</summary>
public sealed class PostgresUsageStore(NpgsqlDataSource dataSource) : IUsageStore, IAsyncDisposable
{
    public async Task<byte[]?> SaltAsync(DateOnly day, DateOnly today, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM usage_salts WHERE day < $1") { Parameters = { new() { Value = today } } });
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "INSERT INTO usage_salts (day, salt) SELECT $1, $2 WHERE $1 >= $3 ON CONFLICT (day) DO NOTHING")
        {
            Parameters = { new() { Value = day }, new() { Value = RandomNumberGenerator.GetBytes(32) }, new() { Value = today } },
        });
        batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT salt FROM usage_salts WHERE day = $1") { Parameters = { new() { Value = day } } });
        return await batch.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    public async Task WriteAsync(IReadOnlyCollection<AndroidSighting> android, IReadOnlyCollection<WebVisit> web, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        if (android.Count > 0)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand("""
                INSERT INTO usage_android (day, install, version_code, version)
                SELECT * FROM unnest($1::date[], $2::uuid[], $3::bigint[], $4::text[])
                ON CONFLICT DO NOTHING
                """)
            {
                Parameters =
                {
                    new() { Value = android.Select(a => a.Day).ToArray() },
                    new() { Value = android.Select(a => a.Install).ToArray() },
                    new() { Value = android.Select(a => a.VersionCode).ToArray() },
                    new() { Value = android.Select(a => a.Version).ToArray() },
                },
            });
        }

        if (web.Count > 0)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand("""
                INSERT INTO usage_web (day, visitor, os)
                SELECT * FROM unnest($1::date[], $2::uuid[], $3::text[])
                ON CONFLICT DO NOTHING
                """)
            {
                Parameters =
                {
                    new() { Value = web.Select(w => w.Day).ToArray() },
                    new() { Value = web.Select(w => w.Visitor).ToArray() },
                    new() { Value = web.Select(w => w.Os).ToArray() },
                },
            });
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
