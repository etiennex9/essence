using Essence.Data;
using Essence.Ingest.Feed;

namespace Essence.Ingest.Tests;

public sealed class FeedParserTests
{
    [Fact]
    public void Parses_the_sample_file()
    {
        var feed = FeedParser.Parse(TestFeeds.Fixture());

        var prices = feed.Stations.SelectMany(s => s.Prices).ToList();
        Assert.Equal(2463, feed.Stations.Count);
        Assert.Equal(2463, feed.Stations.Select(s => s.NaturalKey).Distinct().Count());
        Assert.Equal(7127, prices.Count);
        Assert.Equal(249, prices.Count(p => !p.IsAvailable));
        Assert.All(prices.Where(p => !p.IsAvailable), p => Assert.Null(p.PriceCents));
        Assert.Equal(["Diesel", "Régulier", "Super"], prices.Select(p => p.FuelType).Distinct().Order());
        Assert.Empty(feed.Warnings);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 1, 20, 1, TimeSpan.Zero).AddTicks(7_943_610), feed.GeneratedAt);
        Assert.Equal(TimeSpan.Zero, feed.GeneratedAt.Offset);
    }

    [Fact]
    public void Applies_the_data_quality_rules_to_the_sample_file()
    {
        var feed = FeedParser.Parse(TestFeeds.Fixture());

        var hub = Assert.Single(feed.Stations, s => s.Name == "Hub Régie");
        Assert.Null(hub.Latitude);
        Assert.Null(hub.Longitude);
        Assert.Null(hub.Brand); // "Aucun"
        Assert.Equal(1, feed.Stations.Count(s => s.Latitude is null));
        Assert.Equal(1, feed.Stations.Count(s => s.Name is null));
        Assert.Equal(22 + 19, feed.Stations.Count(s => s.Brand is null));
        Assert.DoesNotContain(feed.Stations, s => s.Brand is "" or "Aucun");
    }

    [Theory]
    [InlineData("199.4¢", 199.4)]
    [InlineData("199.4", 199.4)]
    [InlineData(" 201.0 ¢ ", 201.0)]
    [InlineData("99¢", 99.0)]
    [InlineData("199.45¢", 199.5)]
    [InlineData("9999.9¢", 9999.9)]
    public void Parses_prices(string text, double expected)
    {
        Assert.True(PriceParser.TryParse(text, out var cents));
        Assert.Equal((decimal)expected, cents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("¢")]
    public void Missing_prices_are_null(string? text)
    {
        Assert.True(PriceParser.TryParse(text, out var cents));
        Assert.Null(cents);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("199,4¢")]
    [InlineData("-1.0¢")]
    [InlineData("$1.99")]
    [InlineData("10000.0¢")]
    public void Rejects_unparsable_prices(string text)
    {
        Assert.False(PriceParser.TryParse(text, out _));
    }

    [Fact]
    public void An_unparsable_price_skips_only_that_row_and_counts_a_warning()
    {
        var json = TestFeeds.RawStation("""
            { "Name": "A", "brand": "Shell", "Status": "En opération", "Address": "1 rue X", "PostalCode": "H0H 0H0", "Region": "Montréal",
              "Prices": [ { "GasType": "Régulier", "Price": "1,99$", "IsAvailable": true },
                          { "GasType": "Diesel", "Price": "250.9¢", "IsAvailable": true } ] }
            """);

        var feed = FeedParser.Parse(json);

        var station = Assert.Single(feed.Stations);
        var price = Assert.Single(station.Prices);
        Assert.Equal(new FeedPrice("Diesel", 250.9m, true), price);
        Assert.Equal(["Régulier"], station.SkippedFuelTypes);
        Assert.Single(feed.Warnings);
    }

    [Theory]
    [InlineData("""{ "Name": "", "brand": "", "Status": "En opération", "Address": "1 rue X", "PostalCode": "H0H 0H0", "Region": "R", "Prices": [] }""")]
    [InlineData("""{ "Name": "  ", "brand": "AUCUN", "Status": "En opération", "Address": "1 rue X", "PostalCode": "H0H 0H0", "Region": "R", "Prices": [] }""")]
    [InlineData("""{ "Name": null, "brand": null, "Status": "En opération", "Address": "1 rue X", "PostalCode": "H0H 0H0", "Region": "R" }""")]
    public void Empty_names_and_brands_become_null(string properties)
    {
        var station = Assert.Single(FeedParser.Parse(TestFeeds.RawStation(properties)).Stations);

        Assert.Null(station.Name);
        Assert.Null(station.Brand);
    }

    [Theory]
    [InlineData("[0, 0]")]
    [InlineData("[0.0, 0.0]")]
    [InlineData("[-73.5]")]
    [InlineData("[-73.5, 95]")]
    [InlineData("null")]
    public void Placeholder_or_invalid_coordinates_become_null(string coordinates)
    {
        var json = TestFeeds.RawStation(
            """{ "Name": "A", "Status": "S", "Address": "1 rue X", "PostalCode": "H0H 0H0", "Region": "R", "Prices": [] }""",
            coordinates);

        var station = Assert.Single(FeedParser.Parse(json).Stations);

        Assert.Null(station.Latitude);
        Assert.Null(station.Longitude);
    }

    [Fact]
    public void Coordinates_are_longitude_then_latitude()
    {
        var station = Assert.Single(FeedParser.Parse(TestFeeds.Json(TestFeeds.GeneratedAt)).Stations);

        Assert.Equal(48.246759, station.Latitude);
        Assert.Equal(-79.019574, station.Longitude);
    }

    [Theory]
    [InlineData("""{ "type": "FeatureCollection", "features": [] }""")]
    [InlineData("""{ "type": "FeatureCollection", "metadata": { "generated_at": "2026-10-12T12:00:01Z" }, "features": [] }""")]
    [InlineData("""{ "metadata": { "generated_at": "2026-10-12T12:00:01Z" }, "features": """)]
    [InlineData("<html>Service unavailable</html>")]
    public void Rejects_files_without_generated_at_or_features(string json)
    {
        Assert.Throws<FeedFormatException>(() => FeedParser.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Natural_keys_ignore_case_accents_and_whitespace()
    {
        var key = StationKey.Create("230 av. Murdoch, Rouyn-Noranda", "J9X 1E7");

        Assert.Equal("230 av. murdoch, rouyn-noranda|J9X1E7", key);
        Assert.Equal(key, StationKey.Create("  230  AV. Murdoch,\tRouyn-Noranda ", "j9x1e7"));
        Assert.Equal(key, StationKey.Create("230 av. Murdoch, Rouyn-Noranda", " J9X  1E7 "));
        Assert.Equal(
            StationKey.Create("1506 route 101, Saint-Édouard-de-Fabre", "J0Z 1Z0"),
            StationKey.Create("1506 Route 101, Saint-Edouard-de-Fabre", "J0Z1Z0"));
        Assert.NotEqual(key, StationKey.Create("232 av. Murdoch, Rouyn-Noranda", "J9X 1E7"));
        Assert.NotEqual(key, StationKey.Create("230 av. Murdoch, Rouyn-Noranda", "J9X 1E8"));
    }

    [Fact]
    public void Fold_removes_accents_and_lowercases()
    {
        Assert.Equal("regulier", StationKey.Fold("Régulier"));
        Assert.Equal("montreal", StationKey.Fold("MONTRÉAL"));
        Assert.Equal("", StationKey.Fold(null));
    }
}
