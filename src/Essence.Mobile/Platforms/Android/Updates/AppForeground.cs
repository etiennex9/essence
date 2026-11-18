using Android.App;
using Android.OS;

namespace Essence.Mobile.Updates;

/// <summary>
/// Whether any of the app's screens is showing (started): the map, a widget's settings, Follow where I am. Installing an
/// update closes the app, so one found in the background waits until none is (<see cref="AppUpdater"/>). Registered by
/// <see cref="MainApplication"/>.
/// </summary>
internal sealed class AppForeground : Java.Lang.Object, Android.App.Application.IActivityLifecycleCallbacks
{
    public static AppForeground Instance { get; } = new();

    private int _started;

    public static bool Showing => Volatile.Read(ref Instance._started) > 0;

    public void OnActivityStarted(Activity activity) => Interlocked.Increment(ref _started);

    public void OnActivityStopped(Activity activity)
    {
        if (Interlocked.Decrement(ref _started) < 0)
        {
            Interlocked.Exchange(ref _started, 0);
        }
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState)
    {
    }

    public void OnActivityDestroyed(Activity activity)
    {
    }

    public void OnActivityPaused(Activity activity)
    {
    }

    public void OnActivityResumed(Activity activity)
    {
    }

    public void OnActivitySaveInstanceState(Activity activity, Bundle outState)
    {
    }
}
