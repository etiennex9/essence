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

    /// <summary>The API refused the request as it was asked (400): a value out of range, say.</summary>
    Invalid,

    /// <summary>No road or route to answer with (422), outside Québec included.</summary>
    NoRoute,

    /// <summary>A service the API relies on is down (503): it may answer again shortly.</summary>
    Unavailable,

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

    /// <summary>The most station ids <c>ignoreHours</c> takes (the API's limit).</summary>
    public const int MaxIgnoredHours = 50;

    /// <summary>Where to fill up around a point, for a car (<c>/stations/best</c>).</summary>
    public Task<ApiResult<BestNearby>> GetBestAsync(BestQuery query, CancellationToken cancellationToken) =>
        GetBestAsync(query, [], cancellationToken);

    /// <inheritdoc cref="GetBestAsync(BestQuery, CancellationToken)"/>
    /// <param name="ignoreHours">
    /// Stations whose hours the device corrected: the API never leaves them out for its own hours. None sends no parameter;
    /// past <see cref="MaxIgnoredHours"/>, the first ones.
    /// </param>
    public Task<ApiResult<BestNearby>> GetBestAsync(BestQuery query, IReadOnlyCollection<long> ignoreHours, CancellationToken cancellationToken) =>
        GetAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"stations/best?lat={query.Latitude:0.######}&lon={query.Longitude:0.######}&fuel={Uri.EscapeDataString(query.Fuel)}"
                + $"&fillL={query.FillLitres:0.##}&per100Km={query.LitresPer100Km:0.##}&costco={(query.CostcoMember ? "true" : "false")}"
                + $"&maxDetourMin={query.MaxDetourMinutes}{IgnoreHours(ignoreHours)}"),
            EssenceJsonContext.Default.BestNearby,
            cancellationToken);

    /// <summary>Where to fill up on the way to a destination, for a car (<c>/trips/best</c>).</summary>
    public Task<ApiResult<TripPlan>> GetTripAsync(TripQuery query, CancellationToken cancellationToken) =>
        GetTripAsync(query, [], cancellationToken);

    /// <inheritdoc cref="GetTripAsync(TripQuery, CancellationToken)"/>
    /// <param name="ignoreHours">Stations whose hours the device corrected, as for <see cref="GetBestAsync(BestQuery, IReadOnlyCollection{long}, CancellationToken)"/>.</param>
    public Task<ApiResult<TripPlan>> GetTripAsync(TripQuery query, IReadOnlyCollection<long> ignoreHours, CancellationToken cancellationToken) =>
        GetAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"trips/best?fromLat={query.FromLatitude:0.######}&fromLon={query.FromLongitude:0.######}"
                + $"&toLat={query.ToLatitude:0.######}&toLon={query.ToLongitude:0.######}&fuel={Uri.EscapeDataString(query.Fuel)}"
                + $"&fillL={query.FillLitres:0.##}&per100Km={query.LitresPer100Km:0.##}&costco={(query.CostcoMember ? "true" : "false")}"
                + $"&maxDetourMin={query.MaxDetourMinutes}{(query.RangeKm is { } range ? $"&rangeKm={range}" : "")}&spareKm={query.SpareKm}"
                + $"{IgnoreHours(ignoreHours)}"),
            EssenceJsonContext.Default.TripPlan,
            cancellationToken);

    /// <summary>"&amp;ignoreHours=42,7", the ids once each, at most <see cref="MaxIgnoredHours"/>; "" for none.</summary>
    private static string IgnoreHours(IReadOnlyCollection<long> ids) =>
        ids.Count == 0
            ? ""
            : "&ignoreHours=" + string.Join(',', ids.Distinct().Take(MaxIgnoredHours).Select(id => id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Places in Québec matching what was typed (<c>/places/search</c>), those near <paramref name="near"/> first. The
    /// text goes in the query string, which Essence.Web's nginx doesn't log under <c>/api/places/</c>.
    /// </summary>
    /// <param name="language"><c>fr</c> or <c>en</c>.</param>
    public Task<ApiResult<List<PlaceSuggestion>>> SearchPlacesAsync(
        string text, GeoPoint? near, string language, CancellationToken cancellationToken)
    {
        var bias = near is { } point
            ? string.Create(CultureInfo.InvariantCulture, $"&lat={point.Latitude:0.####}&lon={point.Longitude:0.####}")
            : "";
        return GetAsync(
            $"places/search?q={Uri.EscapeDataString(text)}{bias}&lang={language}", EssenceJsonContext.Default.ListPlaceSuggestion, cancellationToken);
    }

    /// <summary>
    /// What a point is called (<c>/places/reverse</c>): <see cref="ApiError.NoRoute"/> outside Québec,
    /// <see cref="ApiError.NotFound"/> where nothing with an address is near.
    /// </summary>
    public Task<ApiResult<PlaceSuggestion>> GetPlaceAsync(GeoPoint point, string language, CancellationToken cancellationToken) =>
        GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"places/reverse?lat={point.Latitude:0.######}&lon={point.Longitude:0.######}&lang={language}"),
            EssenceJsonContext.Default.PlaceSuggestion,
            cancellationToken);

    /// <summary>Whether trips and the address search are ready, or how far their data is (<c>/services</c>).</summary>
    public Task<ApiResult<DataServicesStatus>> GetServicesAsync(CancellationToken cancellationToken) =>
        GetAsync("services", EssenceJsonContext.Default.DataServicesStatus, cancellationToken);

    /// <summary>
    /// What the app needs of the server's settings (<c>/client-config</c>): its OpenStreetMap application.
    /// <see cref="ApiError.NotFound"/> from a server older than it.
    /// </summary>
    public Task<ApiResult<ClientConfig>> GetClientConfigAsync(CancellationToken cancellationToken) =>
        GetAsync("client-config", EssenceJsonContext.Default.ClientConfig, cancellationToken);

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

    private Task<ApiResult<T>> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        GetAsync(http, path, typeInfo, cancellationToken);

    /// <summary>A GET of JSON as an <see cref="ApiResult{T}"/>; also used for the release notes (<see cref="HttpReleaseNotesSource"/>).</summary>
    internal static async Task<ApiResult<T>> GetAsync<T>(HttpClient http, string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
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
                return new ApiResult<T>(default, response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => ApiError.Invalid,
                    HttpStatusCode.UnprocessableEntity => ApiError.NoRoute,
                    HttpStatusCode.ServiceUnavailable => ApiError.Unavailable,
                    _ => ApiError.Failed,
                });
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

/// <summary>What <see cref="EssenceApiClient.GetBestAsync"/> asks for: where, which fuel, and the car.</summary>
public sealed record BestQuery(
    double Latitude,
    double Longitude,
    string Fuel,
    decimal FillLitres,
    decimal LitresPer100Km,
    bool CostcoMember,
    int MaxDetourMinutes)
{
    /// <summary>The question Best near you asks for a car from a point; the Android widgets ask the same.</summary>
    public static BestQuery For(GeoPoint point, string fuel, CarProfile car) =>
        new(point.Latitude, point.Longitude, fuel, car.FillLitres, car.LitresPer100Km, car.CostcoMember, car.MaxDetourMinutes);
}

/// <summary>What <see cref="EssenceApiClient.GetTripAsync"/> asks for: from where, to where, which fuel, and the car.</summary>
/// <param name="RangeKm">What the dashboard said was left; null when the driver didn't say.</param>
public sealed record TripQuery(
    double FromLatitude,
    double FromLongitude,
    double ToLatitude,
    double ToLongitude,
    string Fuel,
    decimal FillLitres,
    decimal LitresPer100Km,
    bool CostcoMember,
    int MaxDetourMinutes,
    int? RangeKm,
    int SpareKm)
{
    /// <summary>
    /// The question Best on the way asks for a car. <paramref name="rangeKm"/> is what the dashboard said was left: the car's
    /// <see cref="CarProfile.RangeKm"/> in the app; the widgets leave it out, as one reading doesn't last the hours they show.
    /// </summary>
    public static TripQuery For(GeoPoint from, GeoPoint to, string fuel, CarProfile car, int? rangeKm) =>
        new(from.Latitude, from.Longitude, to.Latitude, to.Longitude, fuel, car.FillLitres, car.LitresPer100Km, car.CostcoMember,
            car.MaxDetourMinutes, rangeKm, car.SpareKm);
}

/// <summary>Source-generated (trim-safe) serialization of the API's responses, with the API's web defaults (camelCase).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<NearbyStation>))]
[JsonSerializable(typeof(StationDetail))]
[JsonSerializable(typeof(BestNearby))]
[JsonSerializable(typeof(TripPlan))]
[JsonSerializable(typeof(List<PriceHistoryDto>))]
[JsonSerializable(typeof(List<PlaceSuggestion>))]
[JsonSerializable(typeof(DataServicesStatus))]
[JsonSerializable(typeof(ClientConfig))]
public sealed partial class EssenceJsonContext : JsonSerializerContext;
