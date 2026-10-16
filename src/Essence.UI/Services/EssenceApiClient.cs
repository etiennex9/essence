using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Essence.Contracts;

namespace Essence.UI.Services;

public enum ApiError
{
    None,
    NotFound,
    RateLimited,
    Failed,
}

/// <param name="RetryAfter">With <see cref="ApiError.RateLimited"/>: how long the API asked to wait.</param>
/// <param name="DataAsOf">When the data was imported (the API's <c>Last-Modified</c>), on the endpoints that say so.</param>
public sealed record ApiResult<T>(T? Value, ApiError Error = ApiError.None, TimeSpan? RetryAfter = null, DateTimeOffset? DataAsOf = null)
{
    public bool IsSuccess => Error == ApiError.None;
}

/// <summary>Read calls to Essence.Api. Failures come back as <see cref="ApiResult{T}"/>; only cancellation throws.</summary>
public sealed class EssenceApiClient(HttpClient http)
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(10);

    public Task<ApiResult<List<NearbyStation>>> GetNearbyAsync(
        double latitude, double longitude, double radiusKm, string fuel, int limit, CancellationToken cancellationToken) =>
        GetAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"stations/nearby?lat={latitude:0.######}&lon={longitude:0.######}&radiusKm={radiusKm:0.###}&fuel={Uri.EscapeDataString(fuel)}&limit={limit}"),
            EssenceJsonContext.Default.ListNearbyStation,
            cancellationToken);

    public Task<ApiResult<StationDetail>> GetStationAsync(long id, CancellationToken cancellationToken) =>
        GetAsync(string.Create(CultureInfo.InvariantCulture, $"stations/{id}"), EssenceJsonContext.Default.StationDetail, cancellationToken);

    public Task<ApiResult<List<PriceHistoryDto>>> GetPriceHistoryAsync(
        long id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        GetAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"stations/{id}/prices?from={Uri.EscapeDataString(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}"),
            EssenceJsonContext.Default.ListPriceHistoryDto,
            cancellationToken);

    private async Task<ApiResult<T>> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ApiResult<T>(default, ApiError.NotFound);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new ApiResult<T>(default, ApiError.RateLimited, response.Headers.RetryAfter?.Delta ?? DefaultRetryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ApiResult<T>(default, ApiError.Failed);
            }

            var value = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
            return value is null
                ? new ApiResult<T>(default, ApiError.Failed)
                : new ApiResult<T>(value, DataAsOf: response.Content.Headers.LastModified);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A timeout surfaces as TaskCanceledException without our token being cancelled.
            return new ApiResult<T>(default, ApiError.Failed);
        }
    }
}

/// <summary>Source-generated (trim-safe) serialization of the API's responses, with the API's web defaults (camelCase).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<NearbyStation>))]
[JsonSerializable(typeof(StationDetail))]
[JsonSerializable(typeof(List<PriceHistoryDto>))]
public sealed partial class EssenceJsonContext : JsonSerializerContext;
