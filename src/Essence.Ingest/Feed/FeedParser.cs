using System.Text.Json;
using Essence.Data;

namespace Essence.Ingest.Feed;

/// <summary>Turns the raw JSON into a <see cref="FeedDocument"/>, applying the data-quality rules.</summary>
public static class FeedParser
{
    private const string NoBrand = "Aucun";

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

        var address = StationKey.CollapseWhitespace(p.Address);
        var postalCode = StationKey.CollapseWhitespace(p.PostalCode);
        if (address.Length == 0 && postalCode.Length == 0)
        {
            warnings.Add($"Feature #{index} ({p.Name}) has no address or postal code; skipped.");
            return null;
        }

        var naturalKey = StationKey.Create(address, postalCode);
        var (latitude, longitude) = ParseCoordinates(feature!.Geometry);
        var prices = new List<FeedPrice>(p.Prices?.Count ?? 0);
        var skipped = new List<string>();

        foreach (var price in p.Prices ?? [])
        {
            var fuelType = StationKey.CollapseWhitespace(price?.GasType);
            if (price is null || fuelType.Length == 0)
            {
                warnings.Add($"Station {naturalKey}: a price has no GasType; skipped.");
                continue;
            }

            if (prices.Exists(x => x.FuelType == fuelType) || skipped.Contains(fuelType))
            {
                warnings.Add($"Station {naturalKey}: duplicate {fuelType} price; kept the first.");
                continue;
            }

            if (!PriceParser.TryParse(price.Price, out var cents))
            {
                warnings.Add($"Station {naturalKey}: unparsable {fuelType} price \"{price.Price}\"; skipped.");
                skipped.Add(fuelType);
                continue;
            }

            prices.Add(new FeedPrice(fuelType, cents, price.IsAvailable ?? cents is not null));
        }

        return new FeedStation(
            naturalKey,
            NullIfEmpty(StationKey.CollapseWhitespace(p.Name)),
            NormalizeBrand(p.Brand),
            StationKey.CollapseWhitespace(p.Status),
            address,
            postalCode,
            StationKey.CollapseWhitespace(p.Region),
            latitude,
            longitude,
            prices,
            skipped);
    }

    /// <summary>Missing, out-of-range or 0,0 coordinates (the "Hub Régie" placeholder) become null.</summary>
    private static (double? Latitude, double? Longitude) ParseCoordinates(GeometryDto? geometry)
    {
        if (geometry?.Coordinates is not [var longitude, var latitude, ..])
        {
            return (null, null);
        }

        var valid = double.IsFinite(latitude) && double.IsFinite(longitude)
            && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180
            && !(latitude == 0 && longitude == 0);
        return valid ? (latitude, longitude) : (null, null);
    }

    private static string? NormalizeBrand(string? brand)
    {
        var value = StationKey.CollapseWhitespace(brand);
        return value.Length == 0 || value.Equals(NoBrand, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);
}
