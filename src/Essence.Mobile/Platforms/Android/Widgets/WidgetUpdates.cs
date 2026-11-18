using Android.App;
using Android.App.Job;
using Android.Appwidget;
using Android.Content;
using Essence.Mobile.Services;
using Essence.UI.App;
using Essence.UI.App.Widgets;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using AndroidLog = Android.Util.Log;

namespace Essence.Mobile.Widgets;

/// <summary>
/// When the widgets are refreshed: every 30 minutes while any is placed (prices change through the day; Android may batch
/// it), and now when one is added or set up, on its ↻ button (after a fix, for those that rank from where you are), when the
/// app goes to the background (its cars, places, favorites or fuel may have changed), after the server changes, and with
/// Follow on, when you've moved (<see cref="WidgetLocationReceiver"/>). Each is one job for all widgets, run when there's a
/// network. Android's JobScheduler itself, as for the updates (<see cref="Updates.AppUpdates"/>), not WorkManager:
/// WorkManager ran a refresh that came due in the app's own process when that was still there, and from Android 15 a
/// process kept after leaving the screen has no network, so the widgets said they couldn't be updated.
/// </summary>
public static class WidgetUpdates
{
    // Beside the updates' jobs, far above the ids WorkManager gave its own.
    private const int PeriodicId = 1_045_601;
    private const int NowId = 1_045_602;
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>Kept as it is when already scheduled.</summary>
    public static void Schedule(Context context)
    {
        if (Scheduler(context) is not { } scheduler)
        {
            return;
        }

        ForgetWorkManager(scheduler);
        if (scheduler.GetPendingJob(PeriodicId) is null)
        {
            scheduler.Schedule(Job(context, PeriodicId).SetPeriodic((long)Every.TotalMilliseconds)!.SetPersisted(true)!.Build()!);
        }
    }

    /// <summary>
    /// Replaces a refresh waiting for the network, and stops one running, so that it can't finish after this one and show
    /// older answers.
    /// </summary>
    public static void RefreshNow(Context context)
    {
        if (AnyPlaced(context) && Scheduler(context) is { } scheduler)
        {
            scheduler.Schedule(Job(context, NowId).Build()!);
        }
    }

    /// <summary>The last widget of one kind was removed: the refreshes stop if none of the others is placed.</summary>
    public static void StopIfUnused(Context context)
    {
        if (!AnyPlaced(context))
        {
            Scheduler(context)?.Cancel(PeriodicId);
            WidgetTracking.Update(context, wanted: false);
        }
    }

    public static IEnumerable<(WidgetKind Kind, int Id)> Placed(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null)
        {
            return [];
        }

        return Enum.GetValues<WidgetKind>()
            .SelectMany(kind => (manager.GetAppWidgetIds(EssenceWidget.ComponentFor(context, kind)) ?? []).Select(id => (kind, id)))
            .ToList();
    }

    private static bool AnyPlaced(Context context) => Placed(context).Any();

    private static JobScheduler? Scheduler(Context context) => context.GetSystemService(Context.JobSchedulerService) as JobScheduler;

    private static JobInfo.Builder Job(Context context, int id) =>
        new JobInfo.Builder(id, new ComponentName(context, Java.Lang.Class.FromType(typeof(WidgetRefreshJob))))
            .SetRequiredNetworkType(NetworkType.Any)!;

    /// <summary>
    /// The refreshes an older version left to WorkManager, which the app no longer has: in WorkManager's own namespace from
    /// Android 14, beside these before.
    /// </summary>
    private static void ForgetWorkManager(JobScheduler scheduler)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            scheduler.ForNamespace("androidx.work.systemjobscheduler").CancelAll();
        }

        foreach (var job in scheduler.AllPendingJobs)
        {
            if (job.Service?.ClassName == "androidx.work.impl.background.systemjob.SystemJobService")
            {
                scheduler.Cancel(job.Id);
            }
        }
    }
}

/// <summary>
/// Refreshes every placed widget (<see cref="WidgetUpdates"/>). Runs never overlap: one that a newer refresh replaces is
/// stopped first. Not exported; its Java name is fixed, since Android keeps the jobs by it.
/// </summary>
[Service(Name = "io.github.etiennex9.essence.widgets.WidgetRefreshJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
public sealed class WidgetRefreshJob : JobService
{
    /// <summary>A background fix (5 s) and the API's 20 s.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly Dictionary<int, CancellationTokenSource> _running = [];

    public override bool OnStartJob(JobParameters? parameters)
    {
        if (parameters is null)
        {
            return false;
        }

        var stop = new CancellationTokenSource(Budget);
        lock (_running)
        {
            _running[parameters.JobId] = stop;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await OneAtATime.WaitAsync(stop.Token);
                try
                {
                    await WidgetRefresher.RefreshAsync(ApplicationContext ?? this, stop.Token);
                }
                finally
                {
                    OneAtATime.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Out of time or replaced: what was done stays; the next refresh does the rest.
            }
            catch (Exception ex)
            {
                AndroidLog.Warn("Essence.Widgets", $"Refresh: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (_running)
                {
                    // A refresh that replaced this one has the same id.
                    if (_running.TryGetValue(parameters.JobId, out var current) && current == stop)
                    {
                        _running.Remove(parameters.JobId);
                    }
                }

                stop.Dispose();
            }

            JobFinished(parameters, wantsReschedule: false);
        });
        return true;
    }

    /// <summary>Replaced, or stopped by Android (the network went): the run ends, and one Android stopped runs again later.</summary>
    public override bool OnStopJob(JobParameters? parameters)
    {
        if (parameters is not null)
        {
            lock (_running)
            {
                if (_running.TryGetValue(parameters.JobId, out var stop))
                {
                    stop.Cancel();
                }
            }
        }

        return true;
    }
}

/// <summary>
/// Asks each placed widget's question once (two widgets asking the same share the answer) and shows the answers. With Follow
/// on, those ranking from where you are get a fix of their own first, and the movement trigger is kept to what's placed.
/// </summary>
internal static class WidgetRefresher
{
    /// <summary>How long a refresh waits for a fix of its own in the background.</summary>
    private static readonly TimeSpan FixBudget = TimeSpan.FromSeconds(5);

    public static async Task RefreshAsync(Context context, CancellationToken cancellationToken)
    {
        var placed = WidgetUpdates.Placed(context).ToList();
        var manager = AppWidgetManager.GetInstance(context);
        if (placed.Count == 0 || manager is null)
        {
            return;
        }

        // In Android's language now, which may not be the one the process started with.
        AppCulture.Apply(context);
        var services = IPlatformApplication.Current!.Services;
        var preferences = services.GetRequiredService<IPreferenceStore>();
        var servers = services.GetRequiredService<ServerSettings>();
        var l = services.GetRequiredService<IStringLocalizer<Strings>>();
        var time = services.GetRequiredService<TimeProvider>();
        var hasServer = servers.Current is not null;
        var access = WidgetLocator.Access(context);
        var places = new SavedPlaces(preferences);
        await places.LoadAsync();
        var configs = new Dictionary<int, WidgetConfig>();
        foreach (var (_, id) in placed)
        {
            configs[id] = await WidgetConfigs.ReadAsync(preferences, id);
        }

        var followers = placed.Where(p => UsesYou(p.Kind, configs[p.Id], places)).Select(p => p.Id).ToHashSet();
        var following = hasServer && followers.Count > 0 && access == WidgetLocationAccess.AllTheTime;
        if (following && WidgetLocation.NeedsFix(await LastLocation.ReadAsync(preferences), time.GetUtcNow())
            && await WidgetLocator.TryFixAsync(context, FixBudget, allowGps: false, time, cancellationToken) is { } fix)
        {
            await LastLocation.WriteIfNewerAsync(preferences, fix);
        }

        var rankedFrom = await LastLocation.ReadAsync(preferences);
        var feeds = new WidgetFeeds(
            preferences, hasServer ? servers.CreateApiClient() : new EssenceApiClient(new HttpClient()), l, time, access,
            WidgetLocator.IsLocationOn(context));

        var asked = new Dictionary<string, Task<WidgetView>>();
        foreach (var (kind, id) in placed)
        {
            WidgetView view;
            if (!hasServer)
            {
                view = feeds.NoServer(TitleOf(kind, l));
            }
            else
            {
                var config = configs[id];
                if (kind == WidgetKind.BestNear)
                {
                    var origin = config.Origin ?? DefaultOrigin;
                    view = await Ask(asked, $"best:{origin}", () => feeds.BestNearAsync(origin, cancellationToken));
                }
                else if (kind == WidgetKind.Favorites)
                {
                    view = await Ask(asked, "favorites", () => feeds.FavoritesAsync(cancellationToken));
                }
                else
                {
                    var (from, to) = DefaultRoute(config, places);
                    view = await Ask(asked, $"trip:{from}:{to}", () => feeds.TripAsync(from, to, cancellationToken));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (view.Problem == WidgetProblem.Failed)
            {
                // The last stations stay, with a word that they couldn't be updated. Always a full update: Android redraws a
                // widget from its last one, and a widget that never had one would have no taps, the refresh button included.
                view = WidgetViewCache.Failed(await WidgetViewCache.ReadAsync(preferences, id), view);
            }
            else
            {
                await WidgetViewCache.WriteAsync(preferences, id, view);
            }

            // ↻ locates where the widget follows you, unless there's no permission to (it opens the app then).
            var locate = followers.Contains(id) && access != WidgetLocationAccess.NotAllowed;
            manager.UpdateAppWidget(id, WidgetViews.Build(context, kind, view, locate, manager.GetAppWidgetOptions(id)));
        }

        if (hasServer && followers.Count > 0 && rankedFrom is not null)
        {
            await WidgetLocation.WriteRankedFromAsync(preferences, rankedFrom);
        }

        WidgetTracking.Update(context, following);
    }

    /// <summary>Where you are: the owner's choice for Best near, whose title is Best near you.</summary>
    public static WidgetPlaceRef DefaultOrigin => WidgetPlaceRef.You;

    /// <summary>Whether a widget ranks from where you are, as set up or by default.</summary>
    public static bool UsesYou(WidgetKind kind, WidgetConfig config, SavedPlaces places) => kind switch
    {
        WidgetKind.BestNear => (config.Origin ?? DefaultOrigin).IsYou,
        WidgetKind.Trip => DefaultRoute(config, places).From?.IsYou == true,
        _ => false,
    };

    /// <summary>The route set up, or Home → Work when both are saved; neither leaves it to be chosen.</summary>
    public static (WidgetPlaceRef? From, WidgetPlaceRef? To) DefaultRoute(WidgetConfig config, SavedPlaces places) =>
        config is { From: { } from, To: { } to } ? (from, to)
        : places is { Home: not null, Work: not null } ? (new WidgetPlaceRef(UI.Services.PlaceKind.Home), new WidgetPlaceRef(UI.Services.PlaceKind.Work))
        : (null, null);

    public static string TitleOf(WidgetKind kind, IStringLocalizer<Strings> l) => kind switch
    {
        WidgetKind.BestNear => l["BestNearYou"],
        WidgetKind.Favorites => l["Favorites"],
        _ => l["BestOnTheWay"],
    };

    private static Task<WidgetView> Ask(Dictionary<string, Task<WidgetView>> asked, string key, Func<Task<WidgetView>> ask)
    {
        if (!asked.TryGetValue(key, out var answer))
        {
            asked[key] = answer = ask();
        }

        return answer;
    }
}
