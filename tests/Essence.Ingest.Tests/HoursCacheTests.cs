using System.Text;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Tests;

public sealed class HoursCacheTests : IDisposable
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 11, 12, 7, 31, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "essence-hours-" + Guid.NewGuid().ToString("N"));
    private readonly HoursCache _cache;

    public HoursCacheTests()
    {
        _cache = new HoursCache(Options.Create(new HoursOptions { CachePath = _root }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task The_last_download_kept_reads_back_with_its_version_and_when_it_was_fetched()
    {
        var raw = Encoding.UTF8.GetBytes("[true]");

        await _cache.SaveAsync("costco", raw, HoursVersion.Of(raw), FetchedAt, TestContext.Current.CancellationToken);
        var cached = await _cache.LoadAsync("costco", TestContext.Current.CancellationToken);

        Assert.NotNull(cached);
        Assert.Equal(raw, cached.Raw);
        Assert.Equal(HoursVersion.Of(raw), cached.Version);
        Assert.Equal(FetchedAt, cached.FetchedAt);
        Assert.True(File.Exists(Path.Combine(_root, "costco", "latest.raw")));
    }

    [Fact]
    public async Task A_new_download_keeps_the_one_before_as_the_previous()
    {
        var first = Encoding.UTF8.GetBytes("[true,{\"stlocID\":505}]");
        var second = Encoding.UTF8.GetBytes("[true]");

        await _cache.SaveAsync("costco", first, HoursVersion.Of(first), FetchedAt, TestContext.Current.CancellationToken);
        await _cache.SaveAsync("costco", second, HoursVersion.Of(second), FetchedAt.AddDays(7), TestContext.Current.CancellationToken);

        Assert.Equal(second, (await _cache.LoadAsync("costco", TestContext.Current.CancellationToken))!.Raw);
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(_root, "costco", "previous.raw"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nothing_kept_or_a_download_that_isn_t_the_version_its_meta_says_reads_as_nothing()
    {
        Assert.Null(await _cache.LoadAsync("costco", TestContext.Current.CancellationToken));

        var raw = Encoding.UTF8.GetBytes("[true]");
        await _cache.SaveAsync("costco", raw, HoursVersion.Of(raw), FetchedAt, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "costco", "latest.raw"), "[false]", TestContext.Current.CancellationToken);

        Assert.Null(await _cache.LoadAsync("costco", TestContext.Current.CancellationToken));
    }
}
