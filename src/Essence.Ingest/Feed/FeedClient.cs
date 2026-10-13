using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Essence.Data.Entities;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Feed;

public abstract record FetchResult
{
    private FetchResult()
    {
    }

    /// <summary>304: the CDN still has the version we imported last.</summary>
    public sealed record NotModified : FetchResult;

    /// <summary>Same content as the last import (e.g. a different edge node with another ETag).</summary>
    public sealed record Unchanged(string Sha256) : FetchResult;

    /// <summary>An older version than the last import: the CDN flip-flopping between edge caches.</summary>
    public sealed record Stale(DateTimeOffset GeneratedAt) : FetchResult;

    public sealed record NewVersion(FeedCandidate Candidate) : FetchResult;
}

/// <summary>Fetches the feed and decides whether the response is worth importing.</summary>
public sealed class FeedClient(HttpClient httpClient, IOptions<FeedOptions> options, TimeProvider timeProvider)
{
    private const int MaxDecompressedBytes = 64 * 1024 * 1024;

    /// <param name="lastImported">The newest imported version: its ETag goes in If-None-Match, and older or identical files are discarded.</param>
    public async Task<FetchResult> FetchAsync(FeedVersion? lastImported, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.Url);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        if (lastImported?.ETag is { } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new FetchResult.NotModified();
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var fetchedAt = timeProvider.GetUtcNow();

        // No automatic decompression on the handler: we keep the gzip bytes for the raw archive.
        var isGzip = response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
            || body is [0x1f, 0x8b, ..];
        var json = isGzip ? Decompress(body) : body;
        var gzipBytes = isGzip ? body : Compress(body);

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(json));
        if (sha256 == lastImported?.Sha256)
        {
            return new FetchResult.Unchanged(sha256);
        }

        var document = FeedParser.Parse(json);
        if (document.GeneratedAt <= lastImported?.GeneratedAt)
        {
            return new FetchResult.Stale(document.GeneratedAt);
        }

        return new FetchResult.NewVersion(
            new FeedCandidate(document, gzipBytes, sha256, response.Headers.ETag?.ToString(), fetchedAt));
    }

    /// <summary>Downloads and parses a dated xlsx snapshot, to backfill a version the live feed no longer serves.</summary>
    /// <param name="generatedAt">The time in the file name.</param>
    /// <returns>Null when the server doesn't have that file (wrong guess, or past its retention).</returns>
    public async Task<FeedCandidate?> FetchXlsxAsync(Uri url, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var xlsx = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var fetchedAt = timeProvider.GetUtcNow();
        var document = XlsxParser.Parse(xlsx, generatedAt);
        return new FeedCandidate(
            document, xlsx, Convert.ToHexStringLower(SHA256.HashData(xlsx)), ETag: null, fetchedAt, ImportSource.Xlsx);
    }

    private static byte[] Decompress(byte[] gzip)
    {
        try
        {
            using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
            using var output = new MemoryStream(gzip.Length * 16);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxDecompressedBytes)
                {
                    throw new FeedFormatException($"The feed decompresses to more than {MaxDecompressedBytes} bytes.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new FeedFormatException("The feed is not valid gzip.", ex);
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }
}
