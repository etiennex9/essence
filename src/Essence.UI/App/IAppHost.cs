namespace Essence.UI.App;

/// <summary>
/// What a native host (the Android app) adds to the page: its own version name, the server it talks to, newer builds of the
/// app, and whether its widgets follow where you are. The web app registers none, so components get it with
/// <c>IServiceProvider.GetService</c> and show nothing when it's null.
/// </summary>
public interface IAppHost
{
    /// <summary>
    /// The build's version name, as its release is titled: <c>2026.11.07</c>, or <c>2026.11.07.2</c> for that day's second
    /// release (README → Versions). Null for a build CI didn't version, whose About shows the release notes' day.
    /// </summary>
    string? VersionName { get; }

    /// <summary>The server as shown: its host name, with the port when it isn't the default.</summary>
    string ServerName { get; }

    /// <summary>A newer build of the app, once one is found; null until then.</summary>
    AppRelease? NewerRelease { get; }

    /// <summary>
    /// How far the host is with installing <see cref="NewerRelease"/> itself; null when it can't, and About links to the
    /// release's page instead.
    /// </summary>
    AppInstallState? UpdateInstall { get; }

    /// <summary>Downloads and installs <see cref="NewerRelease"/>, which closes the app; Android may ask first.</summary>
    Task InstallUpdateAsync();

    /// <summary>
    /// Where looking for a newer build is, which About shows while it hasn't found one; null when the build doesn't look,
    /// or hasn't yet.
    /// </summary>
    AppUpdateCheckState? UpdateCheck { get; }

    /// <summary>Looks for a newer build now, as About opens, unless it just did. Never throws.</summary>
    Task CheckForUpdateAsync();

    /// <summary>
    /// Raised when <see cref="NewerRelease"/>, <see cref="UpdateInstall"/>, <see cref="UpdateCheck"/> or
    /// <see cref="WidgetsFollow"/> may have changed, on any thread.
    /// </summary>
    event Action? Changed;

    /// <summary>Opens the host's screen for choosing another server.</summary>
    Task ChangeServerAsync();

    /// <summary>Whether the widgets follow where you are; null when the build can't (About shows nothing then).</summary>
    WidgetFollowState? WidgetsFollow { get; }

    /// <summary>Opens the host's screen for turning it on or off.</summary>
    Task OpenWidgetsFollowAsync();
}

/// <summary>Whether the home-screen widgets follow where you are, as About shows it.</summary>
public enum WidgetFollowState
{
    Off,

    On,

    /// <summary>Turned on, but Android no longer lets the app use the location all the time.</summary>
    Paused,
}

/// <summary>Where looking for a newer build is, as About shows it.</summary>
public enum AppUpdateCheckState
{
    Checking,

    /// <summary>The releases answered: About shows the newer build they have, or that the app is up to date.</summary>
    Checked,

    /// <summary>Offline, or the releases didn't answer: About offers to try again.</summary>
    Failed,
}

/// <summary>Where installing a newer build is, as About shows it.</summary>
public enum AppInstallStage
{
    /// <summary>Found, and installs itself in the background, or now with Install.</summary>
    Ready,

    Downloading,

    /// <summary>Handed to Android, which closes the app to install it.</summary>
    Installing,

    /// <summary>Couldn't be downloaded or installed: About links to the release's page.</summary>
    Failed,
}

/// <param name="Percent">How much of the download is done, while downloading; null when its size isn't known.</param>
public sealed record AppInstallState(AppInstallStage Stage, int? Percent = null);

/// <param name="Version">As shown, e.g. <c>2026.11.07.2</c>.</param>
/// <param name="Url">Where to get it: the release's page.</param>
public sealed record AppRelease(string Version, Uri Url)
{
    /// <summary>
    /// A release's version as shown, from its tag: <c>android-2026.11.07.2</c> is <c>2026.11.07.2</c>. The first releases,
    /// on the day's tag (<c>v2026-11-07</c>), show their day.
    /// </summary>
    public static string VersionOfTag(string? tag) => tag switch
    {
        null => "",
        _ when tag.StartsWith(TagPrefix, StringComparison.Ordinal) => tag[TagPrefix.Length..],
        _ => tag.TrimStart('v'),
    };

    /// <summary>
    /// The version code in the name a release gives its APK: <c>essence-449482.apk</c> is 449482. Null for any other file.
    /// </summary>
    public static long? VersionCodeOfApk(string? name) =>
        name is { } n && n.StartsWith(ApkPrefix, StringComparison.Ordinal) && n.EndsWith(ApkSuffix, StringComparison.Ordinal)
        && n.Length - ApkPrefix.Length - ApkSuffix.Length is > 0 and <= 15
        && long.TryParse(n.AsSpan(ApkPrefix.Length, n.Length - ApkPrefix.Length - ApkSuffix.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var code)
            ? code
            : null;

    /// <summary>
    /// The SHA-256 in GitHub's digest of a release's file (<c>sha256:&lt;64 hex digits&gt;</c>), in lowercase. Null for
    /// another algorithm, or none.
    /// </summary>
    public static string? Sha256OfDigest(string? digest) =>
        digest is { Length: 7 + 64 } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && d.AsSpan(7).ContainsAnyExcept(HexDigits) is false
            ? d[7..].ToLowerInvariant()
            : null;

    private const string TagPrefix = "android-";
    private const string ApkPrefix = "essence-";
    private const string ApkSuffix = ".apk";

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
}
