using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Essence.Data;
using Essence.Ingest.Feed;

namespace Essence.Ingest.Tests;

/// <summary>Builders for feed files and parsed stations.</summary>
internal static class TestFeeds
{
    public static readonly DateTimeOffset GeneratedAt = new(2026, 10, 12, 12, 0, 1, TimeSpan.Zero);

    public static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations.sample.geojson"));

    /// <summary>A feed file with one station per address, each selling Régulier at <paramref name="price"/>.</summary>
    public static byte[] Json(DateTimeOffset generatedAt, decimal price = 199.4m, params string[] addresses)
    {
        if (addresses.Length == 0)
        {
            addresses = ["230 av. Murdoch, Rouyn-Noranda"];
        }

        var document = new
        {
            type = "FeatureCollection",
            metadata = new { generated_at = generatedAt, total_stations = addresses.Length },
            features = addresses.Select(address => new
            {
                type = "Feature",
                geometry = new { type = "Point", coordinates = new[] { -79.019574, 48.246759 } },
                properties = new
                {
                    Name = "Station",
                    brand = "Shell",
                    Status = "En opération",
                    Address = address,
                    PostalCode = "J9X 1E7",
                    Region = "Abitibi-Témiscamingue",
                    Prices = new[] { new { GasType = "Régulier", Price = price.ToString("0.0", CultureInfo.InvariantCulture) + "¢", IsAvailable = true } },
                },
            }),
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    /// <summary>A feed file with a single station whose properties are given as raw JSON.</summary>
    public static byte[] RawStation(string propertiesJson, string coordinates = "[-73.5, 45.5]") =>
        Encoding.UTF8.GetBytes($$"""
            {
              "type": "FeatureCollection",
              "metadata": { "generated_at": "2026-10-12T12:00:01.1234567Z", "total_stations": 1 },
              "features": [ { "type": "Feature", "geometry": { "type": "Point", "coordinates": {{coordinates}} }, "properties": {{propertiesJson}} } ]
            }
            """);

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    public static FeedStation Station(
        string address,
        string? brand = "Shell",
        FeedPrice[]? prices = null,
        string[]? skippedFuelTypes = null) =>
        new(
            StationKey.Create(address, "H2Z 1W7"),
            "Station",
            brand,
            "En opération",
            address,
            "H2Z 1W7",
            "Montréal",
            45.5,
            -73.5,
            prices ?? [Price("Régulier", 199.4m)],
            skippedFuelTypes ?? []);

    public static FeedPrice Price(string fuelType, decimal? cents, bool? isAvailable = null) =>
        new(fuelType, cents, isAvailable ?? cents is not null);

    public static FeedDocument Document(params FeedStation[] stations) => new(GeneratedAt, stations, []);
}
