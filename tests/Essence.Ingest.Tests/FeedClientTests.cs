using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Essence.Ingest.Feed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Ingest.Tests;

public sealed class FeedClientTests
{
    private static readonly DateTimeOffset OldGeneratedAt = TestFeeds.GeneratedAt;
    private static readonly DateTimeOffset NewGeneratedAt = TestFeeds.GeneratedAt.AddMinutes(5);

    private readonly FakeTimeProvider _time = new(NewGeneratedAt.AddSeconds(55));

    private FeedClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new FeedOptions()), _time);

    [Fact]
    public async Task A_304_then_new_old_new_sequence_imports_exactly_once()
    {
        var oldJson = TestFeeds.Json(OldGeneratedAt, 199.4m);
        var newJson = TestFeeds.Json(NewGeneratedAt, 201.9m);
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotModified),
            _ => Gzipped(newJson, "\"new\""),
            _ => Gzipped(oldJson, "\"old\""), // CDN flip-flop: an edge still has the previous file.
            _ => Gzipped(newJson, "\"new-other-edge\""));
        var client = CreateClient(handler);
        var lastImported = new FeedVersion(OldGeneratedAt, Sha256(oldJson), "\"old\"");
        var results = new List<FetchResult>();
        var imports = 0;

        for (var i = 0; i < 4; i++)
        {
            var result = await client.FetchAsync(lastImported, TestContext.Current.CancellationToken);
            results.Add(result);
            if (result is FetchResult.NewVersion { Candidate: var candidate })
            {
                imports++;
                lastImported = new FeedVersion(candidate.GeneratedAt, candidate.Sha256, candidate.ETag);
            }
        }

        Assert.Equal(1, imports);
        Assert.IsType<FetchResult.NotModified>(results[0]);
        var imported = Assert.IsType<FetchResult.NewVersion>(results[1]).Candidate;
        Assert.Equal(OldGeneratedAt, Assert.IsType<FetchResult.Stale>(results[2]).GeneratedAt);
        Assert.IsType<FetchResult.Unchanged>(results[3]);

        // If-None-Match always carries the ETag of the newest imported version; discarded responses don't change it.
        Assert.Equal(["\"old\"", "\"old\"", "\"new\"", "\"new\""], handler.IfNoneMatch);
        Assert.Equal(lastImported, new FeedVersion(NewGeneratedAt, Sha256(newJson), "\"new\""));

        Assert.Equal(NewGeneratedAt, imported.GeneratedAt);
        Assert.Equal(_time.GetUtcNow(), imported.FetchedAt);
        Assert.Equal(55, imported.DelaySeconds);
        Assert.Equal(newJson, Gunzip(imported.GzipBytes));
    }

    [Fact]
    public async Task Without_history_any_file_is_new()
    {
        var json = TestFeeds.Json(OldGeneratedAt);
        var handler = new SequenceHandler(_ => Gzipped(json, "\"v1\""));

        var result = await CreateClient(handler).FetchAsync(null, TestContext.Current.CancellationToken);

        var candidate = Assert.IsType<FetchResult.NewVersion>(result).Candidate;
        Assert.Equal(Sha256(json), candidate.Sha256);
        Assert.Equal("\"v1\"", candidate.ETag);
        Assert.Equal([null], handler.IfNoneMatch);
    }

    [Fact]
    public async Task An_uncompressed_response_is_archived_gzipped()
    {
        var json = TestFeeds.Json(OldGeneratedAt);
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(json) });

        var result = await CreateClient(handler).FetchAsync(null, TestContext.Current.CancellationToken);

        var candidate = Assert.IsType<FetchResult.NewVersion>(result).Candidate;
        Assert.Equal(json, Gunzip(candidate.GzipBytes));
        Assert.Equal(Sha256(json), candidate.Sha256);
    }

    [Fact]
    public async Task The_sample_file_comes_through_gzip()
    {
        var json = TestFeeds.Fixture();
        var handler = new SequenceHandler(_ => Gzipped(json, "\"0x8DE\""));

        var result = await CreateClient(handler).FetchAsync(null, TestContext.Current.CancellationToken);

        var candidate = Assert.IsType<FetchResult.NewVersion>(result).Candidate;
        Assert.Equal(2463, candidate.Document.Stations.Count);
    }

    [Fact]
    public async Task Server_errors_and_bad_bodies_throw()
    {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Gzipped("<html>oops</html>"u8.ToArray(), "\"x\""),
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x1f, 0x8b, 1, 2, 3]) };
                response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            });
        var client = CreateClient(handler);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync(null, ct));
        await Assert.ThrowsAsync<FeedFormatException>(() => client.FetchAsync(null, ct));
        await Assert.ThrowsAsync<FeedFormatException>(() => client.FetchAsync(null, ct));
    }

    private static HttpResponseMessage Gzipped(byte[] json, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestFeeds.Gzip(json)) };
        response.Content.Headers.ContentEncoding.Add("gzip");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string?> IfNoneMatch { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("gzip", Assert.Single(request.Headers.AcceptEncoding).Value);
            IfNoneMatch.Add(request.Headers.TryGetValues("If-None-Match", out var values) ? string.Join(",", values) : null);
            return Task.FromResult(responses[_next++](request));
        }
    }
}
