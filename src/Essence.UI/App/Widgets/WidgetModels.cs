using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.UI.Services;

namespace Essence.UI.App.Widgets;

/// <summary>
/// A place a widget ranks from or goes to. Home and Work are found by their kind (saving Home again gives it a new id),
/// another place by its id, and neither means where the device is: its latest fix (<see cref="LastLocation"/>), which the
/// app, a tap on ↻ or, with Follow on, the widgets themselves keep up to date. Stored as <c>{}</c> since the first version,
/// when it meant where the app was last used.
/// </summary>
public sealed record WidgetPlaceRef(PlaceKind? Kind = null, string? PlaceId = null)
{
    public static WidgetPlaceRef You { get; } = new();

    [JsonIgnore]
    public bool IsYou => Kind is null && PlaceId is null;

    public static WidgetPlaceRef For(SavedPlace place) =>
        place.Kind is PlaceKind.Home or PlaceKind.Work ? new(place.Kind) : new(PlaceKind.Other, place.Id);

    /// <summary>The saved place this refers to, if it's still saved.</summary>
    public SavedPlace? Find(SavedPlaces places) => Kind switch
    {
        PlaceKind.Home => places.Home,
        PlaceKind.Work => places.Work,
        _ when PlaceId is { } id => places.Places.FirstOrDefault(p => p.Id == id),
        _ => null,
    };

    /// <summary>How MapPage's <c>to</c> parameter names it.</summary>
    [JsonIgnore]
    public string? LinkName => Kind switch
    {
        PlaceKind.Home => "home",
        PlaceKind.Work => "work",
        _ => PlaceId,
    };
}

/// <summary>What one widget was set up with; what's left out takes the widget's default.</summary>
/// <param name="Origin">Best near a place: where it ranks from.</param>
/// <param name="From">Best on a frequent route: where it starts.</param>
/// <param name="To">Best on a frequent route: where it goes.</param>
public sealed record WidgetConfig(WidgetPlaceRef? Origin = null, WidgetPlaceRef? From = null, WidgetPlaceRef? To = null);

/// <summary>Each widget's <see cref="WidgetConfig"/>, by Android's widget id, in the app's preferences.</summary>
public static class WidgetConfigs
{
    public static string KeyFor(int widgetId) => $"essence.widget.{widgetId}";

    public static async ValueTask<WidgetConfig> ReadAsync(IPreferenceStore preferences, int widgetId)
    {
        var json = await preferences.GetAsync(KeyFor(widgetId));
        if (string.IsNullOrEmpty(json))
        {
            return new WidgetConfig();
        }

        try
        {
            return JsonSerializer.Deserialize(json, WidgetJsonContext.Default.WidgetConfig) ?? new WidgetConfig();
        }
        catch (JsonException)
        {
            return new WidgetConfig();
        }
    }

    public static ValueTask WriteAsync(IPreferenceStore preferences, int widgetId, WidgetConfig config) =>
        preferences.SetAsync(KeyFor(widgetId), JsonSerializer.Serialize(config, WidgetJsonContext.Default.WidgetConfig));
}

/// <summary>
/// Each widget's last view that wasn't a failure, by Android's widget id: a refresh that fails shows it again with a word that
/// it couldn't be updated, rather than lose the stations (Android redraws a widget from its last full update).
/// </summary>
public static class WidgetViewCache
{
    public static string KeyFor(int widgetId) => $"essence.widget.{widgetId}.view";

    public static async ValueTask<WidgetView?> ReadAsync(IPreferenceStore preferences, int widgetId)
    {
        var json = await preferences.GetAsync(KeyFor(widgetId));
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, WidgetJsonContext.Default.WidgetView);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ValueTask WriteAsync(IPreferenceStore preferences, int widgetId, WidgetView view) =>
        preferences.SetAsync(KeyFor(widgetId), JsonSerializer.Serialize(view, WidgetJsonContext.Default.WidgetView));

    /// <summary>What a failed refresh shows: the last view with the failure in its footer, or the failure alone.</summary>
    public static WidgetView Failed(WidgetView? last, WidgetView failure) =>
        last is null ? failure : last with { Updated = failure.Message ?? failure.Updated };
}

/// <summary>Why a widget shows a message instead of stations.</summary>
public enum WidgetProblem
{
    None,

    /// <summary>The app hasn't been set up with a server yet.</summary>
    NoServer,

    /// <summary>The place it ranks from or goes to isn't saved anymore (or a route has none yet).</summary>
    NoPlace,

    /// <summary>Ranking from where you are, but no location was ever found.</summary>
    NoLocationYet,

    /// <summary>No favorites to show.</summary>
    NoFavorites,

    /// <summary>No station near has a price for the fuel.</summary>
    NoPrice,

    /// <summary>No road between the route's places (outside Québec included).</summary>
    NoRoute,

    /// <summary>Trips aren't available on the server right now.</summary>
    Unavailable,

    /// <summary>The server didn't answer, or asked to slow down.</summary>
    Failed,

    // Added later: cached views keep the names, so new ones go last.

    /// <summary>Ranking from where you are, with no location kept and no permission to find one.</summary>
    LocationNotAllowed,

    /// <summary>A trip from where you are, already at its destination.</summary>
    AtDestination,
}

/// <summary>A station on a widget, ready to show.</summary>
/// <param name="Name">The brand, or "Independent".</param>
/// <param name="Detail">What sets it apart: its distance and saving, its detour, or its address.</param>
/// <param name="Price">Cents per litre ("164.9"), or a dash.</param>
/// <param name="Tap">The page's path a tap opens (MapPage's query parameters).</param>
public sealed record WidgetLine(long StationId, string Name, string Detail, string Price, BrandStyle Brand, string Tap);

/// <summary>What a widget shows: a title, its stations or a message, and when it was updated.</summary>
/// <param name="Tap">The page's path a tap anywhere else opens.</param>
/// <param name="Updated">"Updated 8:10 a.m.", from when it was asked.</param>
public sealed record WidgetView(
    string Title,
    string Fuel,
    IReadOnlyList<WidgetLine> Lines,
    WidgetProblem Problem,
    string? Message,
    string Tap,
    string Updated);

/// <summary>Source-generated (trim-safe) serialization of the widgets' settings.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WidgetConfig))]
[JsonSerializable(typeof(WidgetView))]
internal sealed partial class WidgetJsonContext : JsonSerializerContext;
