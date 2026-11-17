using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <param name="FetchedAt">When it was downloaded.</param>
public sealed record CachedDownload(byte[] Raw, string Version, DateTimeOffset FetchedAt);

/// <summary>
/// Each source's last good download, kept on disk so it can be matched again when the feed's stations change, without
/// asking the source: <c>{CachePath}/{source}/latest.raw</c>, the one before as <c>previous.raw</c>, and
/// <c>meta.json</c> with its version and when it was fetched. Only downloads that were written are kept.
/// </summary>
public sealed class HoursCache(IOptions<HoursOptions> options)
{
    private const string LatestName = "latest.raw";
    private const string PreviousName = "previous.raw";
    private const string MetaName = "meta.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Keeps <paramref name="raw"/> as the latest download, the latest until then as the previous one.</summary>
    public async Task SaveAsync(string source, byte[] raw, string version, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        var folder = Folder(source);
        Directory.CreateDirectory(folder);
        var latest = Path.Combine(folder, LatestName);
        var meta = Path.Combine(folder, MetaName);

        // The meta goes first: a crash in between leaves a meta whose version doesn't match latest.raw, which LoadAsync refuses.
        await WriteAtomicallyAsync(meta, JsonSerializer.SerializeToUtf8Bytes(new Meta(version, fetchedAt), Json), cancellationToken);
        if (File.Exists(latest))
        {
            File.Move(latest, Path.Combine(folder, PreviousName), overwrite: true);
        }

        await WriteAtomicallyAsync(latest, raw, cancellationToken);
    }

    /// <summary>The source's latest download, or null when there's none (or what's there doesn't hold together).</summary>
    public async Task<CachedDownload?> LoadAsync(string source, CancellationToken cancellationToken)
    {
        var folder = Folder(source);
        var latest = Path.Combine(folder, LatestName);
        var metaPath = Path.Combine(folder, MetaName);
        if (!File.Exists(latest) || !File.Exists(metaPath))
        {
            return null;
        }

        Meta? meta;
        try
        {
            meta = JsonSerializer.Deserialize<Meta>(await File.ReadAllBytesAsync(metaPath, cancellationToken), Json);
        }
        catch (JsonException)
        {
            return null;
        }

        var raw = await File.ReadAllBytesAsync(latest, cancellationToken);
        return meta is { Version: { } version } && version == HoursVersion.Of(raw)
            ? new CachedDownload(raw, version, meta.FetchedAt)
            : null;
    }

    private string Folder(string source) => Path.Combine(options.Value.CachePath, source);

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Meta(string? Version, DateTimeOffset FetchedAt);
}
