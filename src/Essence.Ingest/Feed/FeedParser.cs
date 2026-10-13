using System.Text.Json;

namespace Essence.Ingest.Feed;

/// <summary>Turns the raw JSON into a <see cref="FeedDocument"/>, applying the data-quality rules.</summary>
public static class FeedParser
{
    public static FeedDocument Parse(ReadOnlySpan<byte> json)
    {
        FeedDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, FeedJsonContext.Default.FeedDto);
        }
        catch (JsonException ex)
        {
            throw new FeedFormatException("The feed is not valid JSON or doesn't have the expected shape.", ex);
        }

        if (dto?.Metadata?.GeneratedAt is not { } generatedAt)
        {
            throw new FeedFormatException("The feed has no metadata.generated_at.");
        }

        if (dto.Features is not { Count: > 0 } features)
        {
            // Importing an empty file would deactivate every station.
            throw new FeedFormatException("The feed has no features.");
        }

        var warnings = new List<string>();
        var stations = new List<FeedStation>(features.Count);
        for (var i = 0; i < features.Count; i++)
        {
            if (ParseStation(features[i], i, warnings) is { } station)
            {
                stations.Add(station);
            }
        }

        if (dto.Metadata.TotalStations is { } total && total != features.Count)
        {
            warnings.Add($"metadata.total_stations is {total} but the file has {features.Count} features.");
        }

        return new FeedDocument(TruncateToMicroseconds(generatedAt.ToUniversalTime()), stations, warnings);
    }

    private static FeedStation? ParseStation(FeatureDto? feature, int index, List<string> warnings)
    {
        var p = feature?.Properties;
        if (p is null)
        {
            warnings.Add($"Feature #{index} has no properties; skipped.");
            return null;
        }

        // GeoJSON order: [longitude, latitude].
        var (latitude, longitude) = feature!.Geometry?.Coordinates is [var lon, var lat, ..] ? (lat, lon) : ((double?)null, (double?)null);
        return StationRules.Apply(
            new RawStation(
                $"Feature #{index}",
                p.Name,
                p.Brand,
                p.Status ?? "",
                p.Address,
                p.PostalCode,
                p.Region,
                latitude,
                longitude,
                (p.Prices ?? []).Select(x => new RawPrice(x?.GasType, x?.Price, x?.IsAvailable)).ToList(),
                []),
            warnings);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);
}
