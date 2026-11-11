using System.Reflection;

namespace Essence.Mobile.Services;

/// <summary>What the build was given (Essence.Mobile.csproj, README → Versions): nothing of it is in the source.</summary>
/// <param name="DefaultServer">Prefills the server screen at first launch; null in a fork's build.</param>
/// <param name="Repository">owner/name on GitHub, whose releases are checked for a newer build; null when not built by CI.</param>
/// <param name="UpdateCheck">Whether to look for newer builds at all (a Google Play build wouldn't).</param>
/// <param name="BackgroundLocation">
/// Whether the widgets may follow where you are with "Allow all the time" (EssenceBackgroundLocation; a Google Play build
/// wouldn't). Without it, the permission isn't in the manifest at all, and ↻ still locates.
/// </param>
public sealed record BuildInfo(string? DefaultServer, string? Repository, bool UpdateCheck, bool BackgroundLocation)
{
#if ESSENCE_BACKGROUND_LOCATION
    private const bool BuiltWithBackgroundLocation = true;
#else
    private const bool BuiltWithBackgroundLocation = false;
#endif

    public static BuildInfo Current { get; } = Read();

    private static BuildInfo Read()
    {
        var metadata = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Value is { Length: > 0 })
            .ToDictionary(a => a.Key, a => a.Value!);
        return new BuildInfo(
            metadata.GetValueOrDefault("Essence.DefaultServer"),
            metadata.GetValueOrDefault("Essence.Repository"),
            !string.Equals(metadata.GetValueOrDefault("Essence.UpdateCheck"), "false", StringComparison.OrdinalIgnoreCase),
            BuiltWithBackgroundLocation);
    }
}
