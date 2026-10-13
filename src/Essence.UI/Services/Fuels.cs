namespace Essence.UI.Services;

/// <summary>The fuels the feed publishes, spelled as the API returns them (it also accepts them without accents).</summary>
public static class Fuels
{
    public const string Default = "Régulier";

    public static IReadOnlyList<string> All { get; } = ["Régulier", "Super", "Diesel"];

    /// <summary>The matching known fuel, or <see cref="Default"/> for anything else (e.g. a stale saved preference).</summary>
    public static string Normalize(string? fuel) =>
        All.FirstOrDefault(f => string.Equals(f, fuel, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>Position in <see cref="All"/> (used for colors and ordering); unknown fuels sort last.</summary>
    public static int IndexOf(string fuel)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], fuel, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return All.Count;
    }
}
