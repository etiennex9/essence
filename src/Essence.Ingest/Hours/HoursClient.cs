namespace Essence.Ingest.Hours;

/// <summary>
/// The hours sources' HTTP client: its own User-Agent (<see cref="HoursOptions.UserAgent"/>), compressed answers
/// decompressed, and the resilience pipeline's timeouts, without the feed's retries unless asked (see Program.cs).
/// </summary>
public sealed class HoursClient(HttpClient httpClient)
{
    /// <exception cref="HttpRequestException">No answer, or an error status.</exception>
    public async Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
