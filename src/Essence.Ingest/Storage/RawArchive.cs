using System.Globalization;
using Essence.Data.Entities;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Storage;

/// <summary>The <c>Archive</c> configuration section.</summary>
public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    public string Path { get; set; } = "/data/archive";
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Keeps every imported file as received, under <c>{root}/yyyy/MM/dd/stations-{generated_at}.geojson.gz</c>
/// (or <c>.xlsx</c> for a backfilled snapshot).
/// </summary>
public sealed class RawArchive(IOptions<ArchiveOptions> options, TimeProvider timeProvider)
{
    private const string FilePrefix = "stations-";
    private const string FeedSuffix = ".geojson.gz";
    private const string XlsxSuffix = ".xlsx";
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private string Root => options.Value.Path;

    public string GetPath(DateTimeOffset generatedAt, ImportSource source)
    {
        var utc = generatedAt.UtcDateTime;
        var inv = CultureInfo.InvariantCulture;
        return Path.Combine(
            Root,
            utc.ToString("yyyy", inv),
            utc.ToString("MM", inv),
            utc.ToString("dd", inv),
            FilePrefix + utc.ToString(StampFormat, inv) + (source == ImportSource.Xlsx ? XlsxSuffix : FeedSuffix));
    }

    /// <summary>Writes the file atomically (temp file + rename) and returns its path. Overwrites a previous attempt.</summary>
    public async Task<string> SaveAsync(byte[] bytes, DateTimeOffset generatedAt, ImportSource source, CancellationToken cancellationToken)
    {
        var path = GetPath(generatedAt, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    /// <summary>Deletes files generated more than <see cref="ArchiveOptions.RetentionDays"/> ago, then empty folders.</summary>
    /// <returns>The number of files deleted.</returns>
    public int DeleteExpired()
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-options.Value.RetentionDays);
        var deleted = 0;
        foreach (var suffix in (string[])[FeedSuffix, XlsxSuffix])
        {
            foreach (var file in Directory.GetFiles(Root, FilePrefix + "*" + suffix, SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var stamp = name[FilePrefix.Length..^suffix.Length];
                if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var generatedAt)
                    && generatedAt < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
        }

        // Deepest folders first, so a day folder is removed before its month folder is checked.
        foreach (var directory in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return deleted;
    }
}
