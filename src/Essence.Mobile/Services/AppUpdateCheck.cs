using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Essence.UI.App;

namespace Essence.Mobile.Services;

/// <summary>The part of GitHub's <c>releases/latest</c> answer the check reads.</summary>
public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

public sealed record GitHubAsset([property: JsonPropertyName("name")] string? Name);

/// <summary>The newest build found, kept so the offer survives a restart between checks.</summary>
public sealed record KnownRelease(long VersionCode, string Version, string Url);

/// <summary>
/// Looks for a newer build of the app among the GitHub releases of the repository it was built from (README → Versions): the
/// release's APK is <c>essence-{versionCode}.apk</c>, and a higher number than this build's is newer. Checked at start and
/// when the app comes back, at most every <see cref="Interval"/>; found, it shows in About (IAppHost.NewerRelease). It
/// never installs anything: the link opens the release's page.
/// </summary>
public sealed partial class AppUpdateCheck(BuildInfo build, ServerSettings servers, TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private const string KnownKey = "essence.app-update";
    private const string CheckedKey = "essence.app-update-checked";

    private readonly long _running = long.TryParse(AppInfo.Current.BuildString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0;
    private int _checking;

    public AppRelease? Newer { get; private set; }

    /// <summary>Raised when <see cref="Newer"/> changes, on any thread.</summary>
    public event Action? Changed;

    private bool Enabled => build.UpdateCheck && !string.IsNullOrEmpty(build.Repository) && _running > 1;

    /// <summary>Shows what an earlier check found, then checks again if it's time. Never throws.</summary>
    public async Task CheckAsync()
    {
        if (!Enabled || Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            if (Newer is null && ReadKnown() is { } known)
            {
                Offer(known);
            }

            var now = time.GetUtcNow();
            if (long.TryParse(Preferences.Default.Get<string?>(CheckedKey, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var checkedAt)
                && now - DateTimeOffset.FromUnixTimeMilliseconds(checkedAt) < Interval)
            {
                return;
            }

            using var http = servers.CreateProbeClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{build.Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var release = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(), MobileJsonContext.Default.GitHubRelease);
            Preferences.Default.Set(CheckedKey, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            if (release is { HtmlUrl: { } url } && NewestApk(release) is { } versionCode)
            {
                var found = new KnownRelease(versionCode, release.TagName?.TrimStart('v') ?? "", url);
                Preferences.Default.Set(KnownKey, JsonSerializer.Serialize(found, MobileJsonContext.Default.KnownRelease));
                Offer(found);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Offline, or GitHub didn't answer: next time.
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void Offer(KnownRelease known)
    {
        if (known.VersionCode <= _running || !Uri.TryCreate(known.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        var offer = new AppRelease(known.Version, url);
        if (offer == Newer)
        {
            return;
        }

        Newer = offer;
        Changed?.Invoke();
    }

    private static long? NewestApk(GitHubRelease release) =>
        release.Assets?
            .Select(a => a.Name is { } name && ApkName().Match(name) is { Success: true } match
                ? long.Parse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : (long?)null)
            .Max();

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

    [GeneratedRegex(@"^essence-(\d{1,15})\.apk$")]
    private static partial Regex ApkName();
}
