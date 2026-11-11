using System.Text.Json;
using Essence.UI.Services;

namespace Essence.UI.App.Widgets;

/// <summary>How far the widgets that rank from where you are may follow the device.</summary>
public enum WidgetLocationAccess
{
    /// <summary>No location permission: a fix kept from before still ranks; without one, the widget asks to allow it.</summary>
    NotAllowed,

    /// <summary>While the app is in use: the widgets rank from the app's last fix, and ↻ takes a new one.</summary>
    WhileInUse,

    /// <summary>"Allow all the time" and Follow turned on: refreshes take their own fix, and moving refreshes the widgets.</summary>
    AllTheTime,
}

/// <summary>What the widgets know about following the device: the owner's choice, and when a fix is worth a refresh.</summary>
public static class WidgetLocation
{
    /// <summary>
    /// "true" once the user turned on Follow, after reading what it does. Kept apart from the permission: Android 8 and 9 have no
    /// background permission to ask, and with the permission gone the choice stays, so the app can say Follow is paused.
    /// </summary>
    public const string FollowKey = "essence.widget-follow";

    /// <summary>The fix the widgets last ranked from, for <see cref="WorthRefreshing"/>.</summary>
    public const string RankedFromKey = "essence.widget-ranked-from";

    /// <summary>A move this long changes what's near (and the page calls a fix less precise than this approximate).</summary>
    public const double MinMoveMeters = BestOrigin.ApproximateBeyondMeters;

    /// <summary>A start this close to the trip's destination is already there.</summary>
    public const double ThereWithinMeters = 1000;

    /// <summary>A fix this recent is used as is: prices change no more often.</summary>
    public static readonly TimeSpan ReuseFor = RefreshSchedule.PublishInterval;

    /// <summary>Moving refreshes the widgets at most this often, for the same reason.</summary>
    public static readonly TimeSpan MinMoveInterval = RefreshSchedule.PublishInterval;

    /// <summary>A fix older than this (one periodic refresh) has its time shown on the widget.</summary>
    public static readonly TimeSpan ShowAgeAfter = TimeSpan.FromMinutes(30);

    /// <param name="location">Any location permission, approximate included.</param>
    /// <param name="background">"Allow all the time", or, before Android 10, the location permission itself.</param>
    /// <param name="follow">The user turned on Follow (<see cref="FollowKey"/>).</param>
    /// <param name="buildAllows">The build may use the location in the background (a Google Play build doesn't).</param>
    public static WidgetLocationAccess AccessOf(bool location, bool background, bool follow, bool buildAllows) =>
        !location ? WidgetLocationAccess.NotAllowed
        : background && follow && buildAllows ? WidgetLocationAccess.AllTheTime
        : WidgetLocationAccess.WhileInUse;

    /// <summary>A refresh takes a fix of its own when there's none, or the last is older than <see cref="ReuseFor"/>.</summary>
    public static bool NeedsFix(LastFix? stored, DateTimeOffset now) => stored is null || now - stored.At >= ReuseFor;

    /// <summary>Moved at least <see cref="MinMoveMeters"/>, and more than either fix's accuracy (an approximate one's 2 km).</summary>
    public static bool Moved(LastFix from, LastFix to) =>
        Geo.DistanceKm(from.Point, to.Point) * 1000 >= Math.Max(MinMoveMeters, Math.Max(from.AccuracyMeters, to.AccuracyMeters));

    /// <summary>A new fix refreshes the widgets when it moved from where they ranked, and not sooner than <see cref="MinMoveInterval"/>.</summary>
    public static bool WorthRefreshing(LastFix? rankedFrom, LastFix fix) =>
        rankedFrom is null || (Moved(rankedFrom, fix) && fix.At - rankedFrom.At >= MinMoveInterval);

    /// <summary>The trip's start is already at its destination, as far as the fix can tell.</summary>
    public static bool IsThere(LastFix start, GeoPoint destination) =>
        Geo.DistanceKm(start.Point, destination) * 1000 < Math.Max(ThereWithinMeters, start.AccuracyMeters);

    /// <summary>What About shows: on, off, or paused when the permission went since.</summary>
    public static WidgetFollowState FollowStateOf(bool follow, WidgetLocationAccess access) =>
        !follow ? WidgetFollowState.Off
        : access == WidgetLocationAccess.AllTheTime ? WidgetFollowState.On
        : WidgetFollowState.Paused;

    public static async ValueTask<bool> IsFollowOnAsync(IPreferenceStore preferences) =>
        await preferences.GetAsync(FollowKey) == "true";

    public static ValueTask SetFollowAsync(IPreferenceStore preferences, bool on) =>
        preferences.SetAsync(FollowKey, on ? "true" : "false");

    public static async ValueTask<LastFix?> ReadRankedFromAsync(IPreferenceStore preferences)
    {
        var json = await preferences.GetAsync(RankedFromKey);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.LastFix);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ValueTask WriteRankedFromAsync(IPreferenceStore preferences, LastFix fix) =>
        preferences.SetAsync(RankedFromKey, JsonSerializer.Serialize(fix, AppJsonContext.Default.LastFix));
}
