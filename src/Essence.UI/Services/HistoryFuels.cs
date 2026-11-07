namespace Essence.UI.Services;

/// <summary>
/// Which lines a station's price history draws: the map's fuel, plus the fuels added with the chips above the chart, kept
/// on the device through <see cref="IPreferenceStore"/>.
/// </summary>
public sealed class HistoryFuels(IPreferenceStore preferences)
{
    public const string StorageKey = "essence.history-fuels";

    /// <summary>A fuel's name can hold a comma, as the feed spells it, but not a line break: ingestion collapses whitespace.</summary>
    private const char Separator = '\n';

    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loading;

    /// <summary>Reads the saved fuels once; later calls return the same task.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public static bool IsMapFuel(string fuel, string? mapFuel) => string.Equals(fuel, mapFuel, StringComparison.OrdinalIgnoreCase);

    public bool IsShown(string fuel, string? mapFuel) => IsMapFuel(fuel, mapFuel) || _added.Contains(fuel);

    /// <summary>
    /// Adds a fuel, or removes it. False, changing nothing, for the map's fuel: adding it would keep it once the map moves to another.
    /// </summary>
    public async Task<bool> ToggleAsync(string fuel, string? mapFuel)
    {
        await LoadAsync();
        if (IsMapFuel(fuel, mapFuel))
        {
            return false;
        }

        if (!_added.Remove(fuel))
        {
            _added.Add(fuel);
        }

        await preferences.SetAsync(StorageKey, string.Join(Separator, _added));
        return true;
    }

    /// <summary>The key of the map's fuel's line, or null when the chart doesn't have one.</summary>
    public static string? Highlight(PriceChartData chart, string? mapFuel) =>
        chart.Series.FirstOrDefault(s => IsMapFuel(s.Key, mapFuel))?.Key;

    /// <summary>
    /// Whether there's anything to choose from. Without the map's fuel there's no line to start from, so a chart that doesn't
    /// have it shows every line rather than nothing.
    /// </summary>
    public static bool Applies(PriceChartData chart, string? mapFuel) =>
        chart.Series.Count > 1 && Highlight(chart, mapFuel) is not null;

    /// <summary>The lines to draw; <paramref name="chart"/> itself when <see cref="Applies"/> is false.</summary>
    public PriceChartData Filter(PriceChartData chart, string? mapFuel) =>
        Applies(chart, mapFuel) ? HistorySeries.Only(chart, s => IsShown(s.Key, mapFuel)) : chart;

    private async Task LoadCoreAsync()
    {
        if (await preferences.GetAsync(StorageKey) is { Length: > 0 } saved)
        {
            _added.UnionWith(saved.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }
}
