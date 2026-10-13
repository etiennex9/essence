using System.Text.Json.Serialization;

namespace Essence.Ingest.Feed;

// Wire format of stations.geojson. Everything is nullable so that a missing field becomes a warning, not a crash.

internal sealed class FeedDto
{
    public FeedMetadataDto? Metadata { get; init; }
    public List<FeatureDto?>? Features { get; init; }
}

internal sealed class FeedMetadataDto
{
    [JsonPropertyName("generated_at")]
    public DateTimeOffset? GeneratedAt { get; init; }

    [JsonPropertyName("total_stations")]
    public int? TotalStations { get; init; }
}

internal sealed class FeatureDto
{
    public GeometryDto? Geometry { get; init; }
    public StationPropertiesDto? Properties { get; init; }
}

internal sealed class GeometryDto
{
    /// <summary>GeoJSON order: [longitude, latitude].</summary>
    public double[]? Coordinates { get; init; }
}

internal sealed class StationPropertiesDto
{
    public string? Name { get; init; }
    public string? Brand { get; init; }
    public string? Status { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? Region { get; init; }
    public List<PriceDto?>? Prices { get; init; }
}

internal sealed class PriceDto
{
    public string? GasType { get; init; }

    /// <summary>E.g. "199.4¢", or null when unavailable.</summary>
    public string? Price { get; init; }

    public bool? IsAvailable { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FeedDto))]
internal sealed partial class FeedJsonContext : JsonSerializerContext
{
}
