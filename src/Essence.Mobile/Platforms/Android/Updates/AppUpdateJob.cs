using Android.App;
using Android.App.Job;
using Android.Content;
using Essence.Mobile.Services;
using AndroidLog = Android.Util.Log;
using Manifest = Android.Manifest;

// Keeps the update jobs across restarts of the phone (JobInfo.SetPersisted).
[assembly: UsesPermission(Manifest.Permission.ReceiveBootCompleted)]

namespace Essence.Mobile.Updates;

/// <summary>
/// When the app looks for an update in the background and installs it (<see cref="AppUpdater"/>): every 12 hours, and a
/// minute after the app leaves the screen while one is waiting. Both on Wi-Fi (an APK is some 30 MB), with the battery and
/// storage not low. Android's JobScheduler itself, not WorkManager: WorkManager runs a job that's due in the app's own
/// process when that's still there, and from Android 15 a process kept after leaving the screen has no network.
/// </summary>
public static class AppUpdates
{
    // Far above the ids WorkManager gives its own jobs, counting up from 0, in the same app.
    private const int PeriodicId = 1_045_501;
    private const int SoonId = 1_045_502;

    private static readonly TimeSpan AfterLeaving = TimeSpan.FromMinutes(1);

    /// <summary>A failed run is tried again after this, then twice as long each time (Android's backoff).</summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    /// <summary>At each start: kept as it is when already scheduled, and cancelled in a build that doesn't check.</summary>
    public static void Schedule(Context context, bool enabled)
    {
        if (Scheduler(context) is not { } scheduler)
        {
            return;
        }

        if (!enabled)
        {
            scheduler.Cancel(PeriodicId);
            scheduler.Cancel(SoonId);
            return;
        }

        if (scheduler.GetPendingJob(PeriodicId) is null)
        {
            scheduler.Schedule(Job(context, PeriodicId).SetPeriodic((long)AppUpdateCheck.Interval.TotalMilliseconds)!.Build()!);
        }
    }

    /// <summary>The app left the screen with an update waiting: it installs once the app has been away a minute.</summary>
    public static void SoonAfterLeaving(Context context)
    {
        // One waiting (for Wi-Fi, say) stays: it checks that the app isn't showing when it runs.
        if (Scheduler(context) is { } scheduler && scheduler.GetPendingJob(SoonId) is null)
        {
            scheduler.Schedule(Job(context, SoonId).SetMinimumLatency((long)AfterLeaving.TotalMilliseconds)!.Build()!);
        }
    }

    private static JobScheduler? Scheduler(Context context) => context.GetSystemService(Context.JobSchedulerService) as JobScheduler;

    private static JobInfo.Builder Job(Context context, int id) =>
        new JobInfo.Builder(id, new ComponentName(context, Java.Lang.Class.FromType(typeof(AppUpdateJob))))
            .SetRequiredNetworkType(NetworkType.Unmetered)!
            .SetRequiresBatteryNotLow(true)!
            .SetRequiresStorageNotLow(true)!
            .SetBackoffCriteria((long)Backoff.TotalMilliseconds, BackoffPolicy.Exponential)!
            .SetPersisted(true)!;
}

/// <summary>Looks for an update and installs it (<see cref="AppUpdates"/>). Not exported; its Java name is fixed, since Android keeps the jobs by it.</summary>
[Service(Name = "io.github.etiennex9.essence.updates.AppUpdateJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
public sealed class AppUpdateJob : JobService
{
    /// <summary>Android stops a job after 10 minutes.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(9);

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
            var done = true;
            try
            {
                var services = IPlatformApplication.Current!.Services;
                await services.GetRequiredService<AppUpdateCheck>().CheckAsync();
                done = await services.GetRequiredService<AppUpdater>().RunInBackgroundAsync(stop.Token);
            }
            catch (Exception ex)
            {
                AndroidLog.Warn("Essence.Update", $"Job: {ex.GetType().Name}: {ex.Message}");
                done = false;
            }
            finally
            {
                lock (_running)
                {
                    _running.Remove(parameters.JobId);
                }

                stop.Dispose();
            }

            // Out of time, what was downloaded stays if it was all there. A failure (no network after all, say) is tried
            // again after the backoff; Android ignores this for a job it stopped itself.
            JobFinished(parameters, wantsReschedule: !done);
        });
        return true;
    }

    /// <summary>Android stops it (Wi-Fi lost, out of time): the run ends, and Android runs it again later.</summary>
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
