using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.UI.App;

namespace Essence.Mobile.Services;

/// <summary>The part of GitHub's <c>releases/latest</c> answer the check reads.</summary>
public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

/// <param name="Digest">GitHub's <c>sha256:&lt;hex&gt;</c> of the file.</param>
public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("browser_download_url")] string? DownloadUrl = null,
    [property: JsonPropertyName("size")] long? Size = null,
    [property: JsonPropertyName("digest")] string? Digest = null);

/// <summary>
/// The newest build found, kept so the offer survives a restart between checks. What an older build kept has no APK:
/// About links to the release's page until the next check.
/// </summary>
/// <param name="Url">The release's page.</param>
/// <param name="ApkUrl">Its <c>essence-{versionCode}.apk</c>, which <see cref="Updates.AppUpdater"/> downloads.</param>
/// <param name="ApkSha256">The APK's SHA-256 in lowercase hex, when GitHub gave one.</param>
public sealed record KnownRelease(
    long VersionCode, string Version, string Url, string? ApkUrl = null, long? ApkSize = null, string? ApkSha256 = null);

/// <summary>
/// Looks for a newer build of the app among the GitHub releases of the repository it was built from (README → Versions): the
/// latest release's APK is <c>essence-{versionCode}.apk</c>, a higher number than this build's is newer, and its tag gives
/// the version name shown (<see cref="AppRelease.VersionOfTag"/>). Checked at start, when the app comes back and from the
/// background job (<see cref="Updates.AppUpdateJob"/>), at most every <see cref="Interval"/>, and each time About opens, at
/// most every <see cref="AskedInterval"/>; found, it shows in About (IAppHost.NewerRelease), and
/// <see cref="Updates.AppUpdater"/> installs it.
/// </summary>
public sealed class AppUpdateCheck(BuildInfo build, ServerSettings servers, TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>About asks again as it opens; GitHub answers 60 times an hour without a token.</summary>
    public static readonly TimeSpan AskedInterval = TimeSpan.FromMinutes(1);

    private const string KnownKey = "essence.app-update";
    private const string CheckedKey = "essence.app-update-checked";

    private readonly long _running = long.TryParse(AppInfo.Current.BuildString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0;
    private int _checking;

    public AppRelease? Newer { get; private set; }

    /// <summary>The release behind <see cref="Newer"/>, with its APK when it has one.</summary>
    public KnownRelease? Pending { get; private set; }

    /// <summary>This build's version code.</summary>
    public long Running => _running;

    /// <summary>Raised when <see cref="Newer"/> changes, on any thread.</summary>
    public event Action? Changed;

    /// <summary>Where the last check of this run is, as About shows it; null until one asks GitHub.</summary>
    public AppUpdateCheckState? State { get; private set; }

    /// <summary>
    /// Raised when <see cref="State"/> changes, on any thread. Apart from <see cref="Changed"/>, which also gives a failed
    /// install another try.
    /// </summary>
    public event Action? StateChanged;

    public bool Enabled => build.UpdateCheck && !string.IsNullOrEmpty(build.Repository) && _running > 1;

    /// <summary>
    /// Shows what an earlier check found, then checks again if it's time, or if <paramref name="asked"/> (About opened) and
    /// the last check is older than <see cref="AskedInterval"/>. Never throws.
    /// </summary>
    public async Task CheckAsync(bool asked = false)
    {
        if (!Enabled)
        {
            return;
        }

        // Even while another check runs: the background job goes by what it finds here (AppUpdater.Pending).
        if (Newer is null && ReadKnown() is { } known)
        {
            Offer(known);
        }

        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            var now = time.GetUtcNow();
            if (long.TryParse(Preferences.Default.Get<string?>(CheckedKey, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var checkedAt)
                && now - DateTimeOffset.FromUnixTimeMilliseconds(checkedAt) < (asked ? AskedInterval : Interval))
            {
                // Checked a moment ago (only answers are kept): what that found stands.
                if (asked)
                {
                    SetState(AppUpdateCheckState.Checked);
                }

                return;
            }

            SetState(AppUpdateCheckState.Checking);
            using var http = servers.CreateProbeClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, build.UpdateFeed ?? $"https://api.github.com/repos/{build.Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                SetState(AppUpdateCheckState.Failed);
                return;
            }

            var release = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(), MobileJsonContext.Default.GitHubRelease);
            Preferences.Default.Set(CheckedKey, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            if (release is { HtmlUrl: { } url } && NewestApk(release) is { } apk)
            {
                var found = new KnownRelease(
                    apk.VersionCode, AppRelease.VersionOfTag(release.TagName), url, apk.Asset.DownloadUrl, apk.Asset.Size,
                    AppRelease.Sha256OfDigest(apk.Asset.Digest));
                Preferences.Default.Set(KnownKey, JsonSerializer.Serialize(found, MobileJsonContext.Default.KnownRelease));
                Offer(found);
            }

            SetState(AppUpdateCheckState.Checked);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.WebException or IOException or JsonException or TaskCanceledException)
        {
            // Offline (Android's handler throws WebException), or GitHub didn't answer: next time.
            SetState(AppUpdateCheckState.Failed);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void SetState(AppUpdateCheckState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke();
        }
    }

    private void Offer(KnownRelease known)
    {
        if (known.VersionCode <= _running || !Uri.TryCreate(known.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        // An APK that isn't https isn't downloaded, but the release's page is still offered. A test feed (README) may be
        // http in a debug build, which may talk http anyway (MainApplication).
        var apk = Uri.TryCreate(known.ApkUrl, UriKind.Absolute, out var apkUrl) && (apkUrl.Scheme == Uri.UriSchemeHttps || AllowsHttp) ? known : known with { ApkUrl = null };
        var offer = new AppRelease(known.Version, url);
        if (offer == Newer && apk == Pending)
        {
            return;
        }

        Newer = offer;
        Pending = apk;
        Changed?.Invoke();
    }

#if DEBUG
    private const bool AllowsHttp = true;
#else
    private const bool AllowsHttp = false;
#endif

    private static (long VersionCode, GitHubAsset Asset)? NewestApk(GitHubRelease release) =>
        release.Assets?
            .Select(a => AppRelease.VersionCodeOfApk(a.Name) is { } code ? (code, a) : ((long, GitHubAsset)?)null)
            .Where(a => a is not null)
            .MaxBy(a => a!.Value.Item1);

    private static KnownRelease? ReadKnown()
    {
        try
        {
            return Preferences.Default.Get<string?>(KnownKey, null) is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, MobileJsonContext.Default.KnownRelease)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
