namespace Essence.UI;

/// <summary>Settings the host passes to <see cref="ServiceCollectionExtensions.AddEssenceUI"/>.</summary>
public sealed class EssenceUIOptions
{
    /// <summary>Root of Essence.Api, e.g. <c>http://localhost:8080/</c>.</summary>
    public required Uri ApiBaseUrl { get; init; }

    /// <summary>MapLibre style for the light theme (OpenFreeMap: free, no key, OpenStreetMap data).</summary>
    public string MapStyleUrl { get; init; } = "https://tiles.openfreemap.org/styles/positron";

    /// <summary>MapLibre style used when the device prefers a dark color scheme.</summary>
    public string MapDarkStyleUrl { get; init; } = "https://tiles.openfreemap.org/styles/dark";

    /// <summary>
    /// Where release.json and the changelog files are, e.g. the web app's <c>release/</c>. A host that registers its own
    /// <see cref="Services.IReleaseNotesSource"/> first doesn't need it; with neither, the About dialog says it's a development
    /// version.
    /// </summary>
    public Uri? ReleaseNotesUrl { get; init; }
}
