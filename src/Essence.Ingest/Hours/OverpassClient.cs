namespace Essence.Ingest.Hours;

/// <summary>
/// Overpass servers' HTTP client: <see cref="HoursClient"/>'s User-Agent (Overpass refuses generic ones with a 406), but
/// its own timeout (<see cref="OsmOptions.RequestTimeout"/>), a query of all Québec taking the server a minute or more.
/// </summary>
public sealed class OverpassClient(HttpClient httpClient)
{
    /// <summary>Runs <paramref name="query"/> on the server, posted as a form, as Overpass takes it.</summary>
    /// <exception cref="HttpRequestException">No answer, or an error status (504 when the server is too busy).</exception>
    public async Task<byte[]> PostAsync(Uri endpoint, string query, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", query)]);
        using var response = await httpClient.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
