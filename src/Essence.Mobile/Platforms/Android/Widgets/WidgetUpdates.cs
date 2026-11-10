using Android.Appwidget;
using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using Essence.Mobile.Services;
using Essence.UI.App.Widgets;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Java.Util.Concurrent;

namespace Essence.Mobile.Widgets;

/// <summary>
/// When the widgets are refreshed: every 30 minutes while any is placed (prices change through the day; Android may batch
/// it), and now when one is added or set up, on its ↻ button, when the app goes to the background (its cars, places,
/// favorites or fuel may have changed), and after the server changes. Each is one WorkManager job for all widgets, run when
/// there's a network.
/// </summary>
public static class WidgetUpdates
{
    private const string PeriodicName = "essence.widgets.periodic";
    private const string NowName = "essence.widgets.now";
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    public static void Schedule(Context context)
    {
        var request = new PeriodicWorkRequest.Builder(
                Java.Lang.Class.FromType(typeof(WidgetRefreshWorker)), (long)Every.TotalMinutes, TimeUnit.Minutes!)
            .SetConstraints(NeedsNetwork())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniquePeriodicWork(PeriodicName, ExistingPeriodicWorkPolicy.Keep!, (PeriodicWorkRequest)request);
    }

    public static void RefreshNow(Context context)
    {
        if (!AnyPlaced(context))
        {
            return;
        }

        var request = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(WidgetRefreshWorker)))
            .SetConstraints(NeedsNetwork())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniqueWork(NowName, ExistingWorkPolicy.Replace!, (OneTimeWorkRequest)request);
    }

    /// <summary>The last widget of one kind was removed: the refreshes stop if none of the others is placed.</summary>
    public static void StopIfUnused(Context context)
    {
        if (!AnyPlaced(context))
        {
            WorkManager.GetInstance(context).CancelUniqueWork(PeriodicName);
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

    private static Constraints NeedsNetwork() => new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build();
}

/// <summary>
/// Refreshes every placed widget. Its Java name is fixed: WorkManager keeps the jobs it has queued by that name. A run that
/// WorkManager replaces with a newer one (ExistingWorkPolicy.Replace) is stopped, so it can't finish after it and show older
/// answers; runs never overlap.
/// </summary>
[Register("io/github/etiennex9/essence/widgets/WidgetRefreshWorker")]
public sealed class WidgetRefreshWorker(Context context, WorkerParameters workerParams) : Worker(context, workerParams)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly CancellationTokenSource _stop = new(Budget);

    public override Result DoWork()
    {
        try
        {
            OneAtATime.Wait(_stop.Token);
            try
            {
                WidgetRefresher.RefreshAsync(ApplicationContext, _stop.Token).GetAwaiter().GetResult();
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

        return Result.InvokeSuccess()!;
    }

    public override void OnStopped()
    {
        base.OnStopped();
        _stop.Cancel();
    }
}

/// <summary>Asks each placed widget's question once (two widgets asking the same share the answer) and shows the answers.</summary>
internal static class WidgetRefresher
{
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
        var feeds = new WidgetFeeds(
            preferences, hasServer ? servers.CreateApiClient() : new EssenceApiClient(new HttpClient()), l, time);
        var places = new SavedPlaces(preferences);
        await places.LoadAsync();

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
                var config = await WidgetConfigs.ReadAsync(preferences, id);
                if (kind == WidgetKind.BestNear)
                {
                    var origin = config.Origin ?? DefaultOrigin(places);
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

            manager.UpdateAppWidget(id, WidgetViews.Build(context, kind, view));
        }
    }

    /// <summary>Home when it's saved, otherwise where the app was last used.</summary>
    public static WidgetPlaceRef DefaultOrigin(SavedPlaces places) => places.Home is not null ? new WidgetPlaceRef(UI.Services.PlaceKind.Home) : WidgetPlaceRef.LastUsed;

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
