using Essence.Data.Entities;
using Essence.Ingest.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Ingest.Tests;

public sealed class RawArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "essence-archive-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly RawArchive _archive;

    public RawArchiveTests()
    {
        _archive = new RawArchive(Options.Create(new ArchiveOptions { Path = _root, RetentionDays = 30 }), _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Saves_under_a_dated_path_named_after_generated_at()
    {
        var generatedAt = new DateTimeOffset(2026, 10, 12, 1, 20, 1, 794, TimeSpan.Zero);

        var path = await _archive.SaveAsync([1, 2, 3], generatedAt, ImportSource.Feed, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "2026", "10", "12", "stations-20261012T012001Z.geojson.gz"), path);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Saves_a_backfilled_xlsx_next_to_the_feed_files()
    {
        var generatedAt = new DateTimeOffset(2026, 10, 12, 1, 20, 1, TimeSpan.Zero);

        var path = await _archive.SaveAsync([1, 2, 3], generatedAt, ImportSource.Xlsx, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "2026", "10", "12", "stations-20261012T012001Z.xlsx"), path);
    }

    [Fact]
    public async Task Deletes_xlsx_files_past_retention_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var expired = await _archive.SaveAsync([1], new DateTimeOffset(2026, 9, 11, 23, 55, 1, TimeSpan.Zero), ImportSource.Xlsx, ct);
        var kept = await _archive.SaveAsync([2], new DateTimeOffset(2026, 9, 12, 12, 5, 1, TimeSpan.Zero), ImportSource.Xlsx, ct);

        Assert.Equal(1, _archive.DeleteExpired());

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Deletes_files_past_retention_and_empty_folders()
    {
        var ct = TestContext.Current.CancellationToken;
        var expired = await _archive.SaveAsync([1], new DateTimeOffset(2026, 9, 11, 23, 55, 1, TimeSpan.Zero), ImportSource.Feed, ct);
        var kept = await _archive.SaveAsync([2], new DateTimeOffset(2026, 9, 12, 12, 5, 1, TimeSpan.Zero), ImportSource.Feed, ct);

        Assert.Equal(1, _archive.DeleteExpired());

        Assert.False(File.Exists(expired));
        Assert.False(Directory.Exists(Path.GetDirectoryName(expired)));
        Assert.True(File.Exists(kept));
        Assert.Equal(0, _archive.DeleteExpired());
    }
}
