using System.Globalization;
using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Essence.Mobile.Services;
using Essence.UI.App;
using AndroidLog = Android.Util.Log;
using Manifest = Android.Manifest;

#if ESSENCE_SELF_UPDATE
// Only in builds that look for updates (Essence.Mobile.csproj's EssenceUpdateCheck; a Google Play build wouldn't). The second
// lets Android 12+ install the app's own update without asking, once installing from Essence is allowed.
[assembly: UsesPermission(Manifest.Permission.RequestInstallPackages)]
[assembly: UsesPermission(Manifest.Permission.UpdatePackagesWithoutUserAction)]
#endif

namespace Essence.Mobile.Updates;

/// <summary>
/// Installs the newer build <see cref="AppUpdateCheck"/> found: downloads its APK into the cache, checks it against the size
/// and SHA-256 the release lists, and hands it to Android's PackageInstaller, which also checks it's signed with this app's
/// key. In the background (<see cref="AppUpdateJob"/>, on Wi-Fi), only while none of the app's screens shows, since
/// installing closes the app, and only where Android doesn't ask: from Android 12, once installing from Essence is allowed.
/// Otherwise the APK waits for About's Install, where Android asks first if it must (the first time, to allow installing
/// from Essence). README → Versions.
/// </summary>
public sealed class AppUpdater
{
    internal const string InteractiveExtra = "essence.update.interactive";
    internal const string VersionExtra = "essence.update.version";

    /// <summary>A build that couldn't be installed quietly: it waits for Install, rather than being tried every time.</summary>
    private const string NeedsTapKey = "essence.app-update.needs-tap";

    private const string LogTag = "Essence.Update";

    /// <summary>For Install's download: the background's is the job's to stop.</summary>
    private static readonly TimeSpan InstallBudget = TimeSpan.FromMinutes(10);

    /// <summary>A session Android hasn't finished in this long is given up: a new one may start.</summary>
    private static readonly TimeSpan StaleSession = TimeSpan.FromMinutes(10);

    private static readonly AppInstallState Ready = new(AppInstallStage.Ready);
    private static readonly AppInstallState InstallingState = new(AppInstallStage.Installing);

#if ESSENCE_SELF_UPDATE
    private const bool Installs = true;
#else
    private const bool Installs = false;
#endif

    private readonly AppUpdateCheck _check;
    private readonly AppHttpHandler _http;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private volatile AppInstallState? _stage;

    public AppUpdater(AppUpdateCheck check, AppHttpHandler http)
    {
        _check = check;
        _http = http;
        // Android restarts the app when installing from it is allowed, in the middle of Install: it's still installing.
        if (Installs && SessionUnderway(Platform.AppContext))
        {
            _stage = InstallingState;
        }

        _check.Changed += () =>
        {
            // A newer build than the one that failed gets its own try.
            if (_stage?.Stage == AppInstallStage.Failed)
            {
                _stage = null;
            }

            Changed?.Invoke();
        };
    }

    /// <summary>Null when there's nothing to install, or this build can't: About links to the release's page then.</summary>
    public AppInstallState? State => Installs && _check.Pending is { ApkUrl: not null } ? _stage ?? Ready : null;

    /// <summary>Raised when <see cref="State"/> changes, on any thread.</summary>
    public event Action? Changed;

    private static string Folder => Path.Combine(FileSystem.CacheDirectory, "updates");

    /// <summary>
    /// About's Install: downloads on any network if it must, then installs, which closes the app. Says Installing at once,
    /// which also takes the button away: checking an APK already downloaded takes a few seconds, and a second tap would
    /// install it twice.
    /// </summary>
    public Task InstallAsync()
    {
        if (State is not { Stage: AppInstallStage.Ready or AppInstallStage.Failed })
        {
            return Task.CompletedTask;
        }

        Set(InstallingState);
        return Task.Run(async () =>
        {
            using var budget = new CancellationTokenSource(InstallBudget);
            await RunAsync(interactive: true, budget.Token);
        });
    }

    /// <summary>
    /// From <see cref="AppUpdateJob"/>: downloads, and installs if it can without asking. False when the download or the
    /// install failed, which is worth another try soon.
    /// </summary>
    public Task<bool> RunInBackgroundAsync(CancellationToken cancellationToken) => RunAsync(interactive: false, cancellationToken);

    /// <summary>Deletes the APKs of builds no newer than this one (installed since), and downloads left halfway.</summary>
    public void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                var stale = Path.GetExtension(file) == ".part"
                    ? File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - InstallBudget
                    : AppRelease.VersionCodeOfApk(Path.GetFileName(file)) is not { } code || code <= _check.Running;
                if (stale)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // Next time.
        }
    }

    /// <summary>False when it failed: About then links to the release's page, or the job tries again.</summary>
    private async Task<bool> RunAsync(bool interactive, CancellationToken cancellationToken)
    {
        if (State is null || _check.Pending is not { ApkUrl: { } url } release)
        {
            return true;
        }

        var context = Platform.AppContext;
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            // Handed to Android already: its answer comes to AppInstallReceiver.
            if (!interactive && _stage?.Stage == AppInstallStage.Installing)
            {
                return true;
            }

            var apk = await DownloadAsync(release, url, cancellationToken);
            if (!interactive && (!CanInstallQuietly(context, release.VersionCode) || AppForeground.Showing))
            {
                Set(null);
                return true;
            }

            Set(InstallingState);
            if (!SessionUnderway(context))
            {
                Commit(context, apk, release.VersionCode, interactive);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Stopped by Android, offline (Android's handler throws WebException), a download that didn't check out, or
            // Android refused the session. Never thrown on: the job would fail, and Install has nobody to throw to.
            AndroidLog.Warn(LogTag, $"Build {release.VersionCode}: {ex.GetType().Name}: {ex.Message}");
            Set(interactive ? new AppInstallState(AppInstallStage.Failed) : null);
            return false;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>The APK, downloaded once: a later run, or Install after the background's download, uses the same file.</summary>
    private async Task<string> DownloadAsync(KnownRelease release, string url, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"essence-{release.VersionCode.ToString(CultureInfo.InvariantCulture)}.apk");
        if (File.Exists(path) && await MatchesAsync(path, release, cancellationToken))
        {
            return path;
        }

        // An older newer build's, or a download that didn't check out.
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            File.Delete(file);
        }

        Set(new AppInstallState(AppInstallStage.Downloading));
        var part = path + ".part";
        try
        {
            using var http = new HttpClient(_http, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = release.ApkSize ?? response.Content.Headers.ContentLength;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                int? shown = null;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    done += read;
                    if (done > total)
                    {
                        throw new InvalidDataException("The APK is larger than the release says.");
                    }

                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (total is long size && size > 0 && (int)(done * 100 / size) is var percent && percent != shown)
                    {
                        shown = percent;
                        Set(new AppInstallState(AppInstallStage.Downloading, percent));
                    }
                }
            }

            if ((total is { } expectedSize && done != expectedSize)
                || (release.ApkSha256 is { } expected && Convert.ToHexStringLower(sha.GetHashAndReset()) != expected))
            {
                throw new InvalidDataException("The APK isn't the one the release lists.");
            }

            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }

    private static async Task<bool> MatchesAsync(string path, KnownRelease release, CancellationToken cancellationToken)
    {
        if (release.ApkSize is { } size && new FileInfo(path).Length != size)
        {
            return false;
        }

        if (release.ApkSha256 is not { } expected)
        {
            // Only a download that completed was moved to its name.
            return true;
        }

        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) == expected;
    }

    /// <summary>
    /// Without asking, which Android allows an app updating itself from Android 12, once the user allowed installing from it
    /// (and this build didn't already have to ask).
    /// </summary>
    private static bool CanInstallQuietly(Context context, long versionCode) =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
        && context.PackageManager?.CanRequestPackageInstalls() == true
        && Preferences.Default.Get(NeedsTapKey, 0L) != versionCode;

    private static void Commit(Context context, string apk, long versionCode, bool interactive)
    {
        var installer = context.PackageManager!.PackageInstaller;
        var length = new FileInfo(apk).Length;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        parameters.SetSize(length);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            parameters.SetRequireUserAction((int)PackageInstallUserAction.NotRequired);
        }

        var id = installer.CreateSession(parameters);
        try
        {
            using var session = installer.OpenSession(id);
            using (var output = session.OpenWrite("essence.apk", 0, length))
            using (var input = File.OpenRead(apk))
            {
                input.CopyTo(output);
                session.Fsync(output);
            }

            var status = new Intent(context, typeof(AppInstallReceiver))
                .PutExtra(InteractiveExtra, interactive)
                .PutExtra(VersionExtra, versionCode);
            // Mutable: Android adds the result to it.
            var pending = PendingIntent.GetBroadcast(
                context,
                id,
                status,
                OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent : PendingIntentFlags.UpdateCurrent)!;
            session.Commit(pending.IntentSender!);
        }
        catch
        {
            installer.AbandonSession(id);
            throw;
        }
    }

    /// <summary>
    /// Whether Android is still on an install the app handed it (committed, unfinished): another would install the update
    /// twice, closing the app each time. One left unfinished for <see cref="StaleSession"/> is given up. Android 10+.
    /// </summary>
    private static bool SessionUnderway(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) || context.PackageManager?.PackageInstaller is not { } installer)
        {
            return false;
        }

        var underway = false;
        foreach (var session in installer.MySessions)
        {
            if (!session.IsCommitted)
            {
                continue;
            }

            if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(session.UpdatedMillis) < StaleSession)
            {
                underway = true;
                continue;
            }

            try
            {
                installer.AbandonSession(session.SessionId);
            }
            catch (Java.Lang.Exception)
            {
                // Finished in the meantime, or no longer the app's to abandon.
            }
        }

        return underway;
    }

    /// <summary>Android's answer to <see cref="Commit"/>, from <see cref="AppInstallReceiver"/>.</summary>
    internal void OnStatus(Context context, Intent intent)
    {
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        var interactive = intent.GetBooleanExtra(InteractiveExtra, false);
        var versionCode = intent.GetLongExtra(VersionExtra, 0);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
                // Android asks first: while the app shows, now; else Install asks later, and this build isn't tried
                // quietly again.
                if (AppForeground.Showing && ConfirmationOf(intent) is { } confirm)
                {
                    if (Platform.CurrentActivity is { } activity)
                    {
                        activity.StartActivity(confirm);
                    }
                    else
                    {
                        context.StartActivity(confirm.AddFlags(ActivityFlags.NewTask));
                    }

                    return;
                }

                Preferences.Default.Set(NeedsTapKey, versionCode);
                Abandon(context, intent);
                Set(null);
                break;

            case PackageInstallStatus.Success:
                // Android closes the app right after, if it hasn't already.
                Set(null);
                break;

            case PackageInstallStatus.FailureAborted:
                // Cancel, or Back, on Android's question.
                Set(null);
                break;

            default:
                AndroidLog.Warn(LogTag, $"Build {versionCode} not installed: {status}, {intent.GetStringExtra(PackageInstaller.ExtraStatusMessage)}");
                if (status == PackageInstallStatus.FailureInvalid)
                {
                    // Downloaded again next time.
                    DeleteApk(versionCode);
                }

                if (interactive)
                {
                    Set(new AppInstallState(AppInstallStage.Failed));
                }
                else
                {
                    Preferences.Default.Set(NeedsTapKey, versionCode);
                    Set(null);
                }

                break;
        }
    }

    private void Set(AppInstallState? stage)
    {
        _stage = stage;
        Changed?.Invoke();
    }

    private static Intent? ConfirmationOf(Intent intent)
    {
        // The typed overload is Android 13's, and broken there.
#pragma warning disable CA1422
        return intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422
    }

    private static void Abandon(Context context, Intent intent)
    {
        var id = intent.GetIntExtra(PackageInstaller.ExtraSessionId, -1);
        try
        {
            if (id >= 0)
            {
                context.PackageManager?.PackageInstaller.AbandonSession(id);
            }
        }
        catch (Java.Lang.SecurityException)
        {
            // Already gone.
        }
    }

    private static void DeleteApk(long versionCode)
    {
        try
        {
            File.Delete(Path.Combine(Folder, $"essence-{versionCode.ToString(CultureInfo.InvariantCulture)}.apk"));
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Android's answer to an install session (<see cref="AppUpdater"/>). Not exported; its Java name is fixed.</summary>
[BroadcastReceiver(Name = "io.github.etiennex9.essence.updates.AppInstallReceiver", Exported = false)]
public sealed class AppInstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null && intent is not null && IPlatformApplication.Current?.Services.GetService<AppUpdater>() is { } updater)
        {
            updater.OnStatus(context, intent);
        }
    }
}
