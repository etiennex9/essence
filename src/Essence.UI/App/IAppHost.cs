namespace Essence.UI.App;

/// <summary>
/// What a native host (the Android app) adds to the page: the server it talks to, newer builds of the app, and whether its
/// widgets follow where you are. The web app registers none, so components get it with <c>IServiceProvider.GetService</c>
/// and show nothing when it's null.
/// </summary>
public interface IAppHost
{
    /// <summary>The server as shown: its host name, with the port when it isn't the default.</summary>
    string ServerName { get; }

    /// <summary>A newer build of the app, once one is found; null until then.</summary>
    AppRelease? NewerRelease { get; }

    /// <summary>Raised when <see cref="NewerRelease"/> or <see cref="WidgetsFollow"/> may have changed, on any thread.</summary>
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

/// <param name="Version">As shown, e.g. <c>2026-11-07</c>.</param>
/// <param name="Url">Where to get it: the release's page.</param>
public sealed record AppRelease(string Version, Uri Url);
