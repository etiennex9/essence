using Essence.Data;

namespace Essence.Ingest.Feed;

/// <summary>A station as read from either file format, before the data-quality rules.</summary>
/// <param name="Location">Where it is in the file, for warnings, e.g. "Feature #12" or "Row 14".</param>
/// <param name="Status">Null when the format has no status column.</param>
internal sealed record RawStation(
    string Location,
    string? Name,
    string? Brand,
    string? Status,
    string? Address,
    string? PostalCode,
    string? Region,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<RawPrice> Prices,
    IReadOnlyList<string> NotAvailableFuelTypes);

/// <param name="IsAvailable">Null when the file doesn't say: available if there is a price.</param>
internal sealed record RawPrice(string? FuelType, string? Price, bool? IsAvailable);

/// <summary>The data-quality rules shared by <see cref="FeedParser"/> and <see cref="XlsxParser"/>.</summary>
internal static class StationRules
{
    private const string NoBrand = "Aucun";

    /// <summary>Cleans up a station, or returns null (with a warning) when it can't be identified.</summary>
    public static FeedStation? Apply(RawStation raw, List<string> warnings)
    {
        var address = StationKey.CollapseWhitespace(raw.Address);
        var postalCode = StationKey.CollapseWhitespace(raw.PostalCode);
        if (address.Length == 0 && postalCode.Length == 0)
        {
            warnings.Add($"{raw.Location} ({raw.Name}) has no address or postal code; skipped.");
            return null;
        }

        var naturalKey = StationKey.Create(address, postalCode);
        var (latitude, longitude) = ValidCoordinates(raw.Latitude, raw.Longitude);
        var prices = new List<FeedPrice>(raw.Prices.Count);
        var skipped = new List<string>();

        foreach (var price in raw.Prices)
        {
            var fuelType = StationKey.CollapseWhitespace(price.FuelType);
            if (fuelType.Length == 0)
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
            NullIfEmpty(StationKey.CollapseWhitespace(raw.Name)),
            NormalizeBrand(raw.Brand),
            raw.Status is null ? null : StationKey.CollapseWhitespace(raw.Status),
            address,
            postalCode,
            StationKey.CollapseWhitespace(raw.Region),
            latitude,
            longitude,
            prices,
            skipped,
            raw.NotAvailableFuelTypes.Select(StationKey.CollapseWhitespace).Where(f => f.Length > 0).ToList());
    }

    /// <summary>Missing, out-of-range or 0,0 coordinates (the "Hub Régie" placeholder) become null.</summary>
    private static (double? Latitude, double? Longitude) ValidCoordinates(double? latitude, double? longitude)
    {
        if (latitude is not { } lat || longitude is not { } lon)
        {
            return (null, null);
        }

        var valid = double.IsFinite(lat) && double.IsFinite(lon)
            && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180
            && !(lat == 0 && lon == 0);
        return valid ? (lat, lon) : (null, null);
    }

    private static string? NormalizeBrand(string? brand)
    {
        var value = StationKey.CollapseWhitespace(brand);
        return value.Length == 0 || value.Equals(NoBrand, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
