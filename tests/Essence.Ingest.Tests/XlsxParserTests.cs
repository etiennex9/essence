using Essence.Ingest.Diffing;
using Essence.Ingest.Feed;
using static Essence.Ingest.Tests.TestFeeds;

namespace Essence.Ingest.Tests;

public sealed class XlsxParserTests
{
    private static readonly DateTimeOffset FileNameTime = new(2026, 10, 12, 12, 0, 1, TimeSpan.Zero);

    [Fact]
    public void Parses_the_sample_file()
    {
        var feed = XlsxParser.Parse(PairXlsx(), PairGeneratedAt);

        Assert.Equal(PairGeneratedAt, feed.GeneratedAt);
        Assert.Equal(2463, feed.Stations.Count);
        Assert.Equal(2463, feed.Stations.Select(s => s.NaturalKey).Distinct().Count());
        Assert.Equal(6876, feed.Stations.Sum(s => s.Prices.Count));
        Assert.Equal(513, feed.Stations.Sum(s => s.NotAvailableFuelTypes.Count)); // 251 unavailable + 262 not sold, per the JSON.
        Assert.All(feed.Stations, s => Assert.Null(s.Status));
        Assert.Empty(feed.Warnings);

        var first = feed.Stations[0];
        Assert.Equal("2642-4101 Québec Inc.", first.Name);
        Assert.Equal("Shell", first.Brand);
        Assert.Equal("230 av. Murdoch, Rouyn-Noranda", first.Address);
        Assert.Equal("J9X 1E7", first.PostalCode);
        Assert.Equal("Abitibi-Témiscamingue", first.Region);
        Assert.Equal(48.246759, first.Latitude);
        Assert.Equal(-79.019574, first.Longitude);
        Assert.Equal([Price("Régulier", 199.4m), Price("Super", 223.9m), Price("Diesel", 295.9m)], first.Prices);
    }

    [Fact]
    public void The_xlsx_and_the_json_of_the_same_version_diff_to_nothing()
    {
        var json = FeedParser.Parse(PairJson());
        var xlsx = XlsxParser.Parse(PairXlsx(), PairGeneratedAt);

        // The JSON as the stored state.
        var stations = json.Stations
            .Select((s, i) => new StationSnapshot(i + 1, s.NaturalKey, s.Name, s.Brand, s.Status!, s.Address, s.PostalCode,
                s.Region, s.Latitude, s.Longitude, IsActive: true))
            .ToList();
        var historyId = 0L;
        var prices = json.Stations
            .SelectMany((s, i) => s.Prices.Select(p => new PriceSnapshot(i + 1, p.FuelType, p.PriceCents, p.IsAvailable, ++historyId)))
            .ToList();

        var changes = FeedDiffer.Diff(stations, prices, xlsx);

        Assert.Equal(json.GeneratedAt.AddTicks(-(json.GeneratedAt.Ticks % TimeSpan.TicksPerSecond)), xlsx.GeneratedAt);
        Assert.Empty(changes.NewStations);
        Assert.Empty(changes.UpdatedStations);
        Assert.Empty(changes.DeactivatedStationIds);
        Assert.Empty(changes.PriceChanges);
        Assert.Empty(changes.ClosedPrices);
        Assert.Empty(changes.Warnings);
        Assert.Equal(json.Stations.Count, changes.StationsTotal);
    }

    [Fact]
    public void Maps_columns_by_header_and_reads_sparse_cells()
    {
        var xlsx = Xlsx(
            ["Adresse", "Code postal", "Nom", "Bannière", "Région", "Longitude", "Latitude", "Prix Diesel", "Prix Super", "Prix Régulier"],
            ["1 rue X, Montréal", "H0H 0H0", "Station A", "Aucun", "Montréal", -73.5, 45.5, "N/D", null, "199.4¢"],
            [],
            ["Données générées le : 2026-10-12 à 12:00:01 UTC"],
            ["Total de stations : 1"]);

        var feed = XlsxParser.Parse(xlsx, FileNameTime);

        var station = Assert.Single(feed.Stations);
        Assert.Equal("Station A", station.Name);
        Assert.Null(station.Brand);
        Assert.Null(station.Status);
        Assert.Equal(45.5, station.Latitude);
        Assert.Equal(-73.5, station.Longitude);
        Assert.Equal([Price("Régulier", 199.4m)], station.Prices);
        Assert.Equal(["Diesel"], station.NotAvailableFuelTypes); // The blank Super cell means "not sold".
        Assert.Equal(FileNameTime, feed.GeneratedAt);
        Assert.Empty(feed.Warnings);
    }

    [Fact]
    public void The_footer_time_wins_over_the_file_name_and_a_mismatch_warns()
    {
        var xlsx = Xlsx(XlsxHeader, Row("1 rue X"), ["Données générées le : 2026-10-12 à 12:00:00 UTC"]);

        var feed = XlsxParser.Parse(xlsx, FileNameTime);

        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), feed.GeneratedAt);
        Assert.Single(feed.Warnings);
    }

    [Fact]
    public void Without_a_footer_time_the_file_name_is_used()
    {
        var feed = XlsxParser.Parse(Xlsx(XlsxHeader, Row("1 rue X")), FileNameTime);

        Assert.Equal(FileNameTime, feed.GeneratedAt);
        Assert.Empty(feed.Warnings);
    }

    [Fact]
    public void A_wrong_station_total_warns()
    {
        var feed = XlsxParser.Parse(Xlsx(XlsxHeader, Row("1 rue X"), Row("2 rue X"), ["Total de stations : 3"]), FileNameTime);

        Assert.Equal(2, feed.Stations.Count);
        Assert.Single(feed.Warnings);
    }

    [Fact]
    public void An_unparsable_price_skips_only_that_fuel()
    {
        var feed = XlsxParser.Parse(Xlsx(XlsxHeader, Row("1 rue X", regular: "1,99$")), FileNameTime);

        var station = Assert.Single(feed.Stations);
        Assert.Equal(["Régulier"], station.SkippedFuelTypes);
        Assert.DoesNotContain(station.Prices, p => p.FuelType == "Régulier");
        Assert.Single(feed.Warnings);
    }

    [Fact]
    public void A_row_without_address_is_skipped_without_ending_the_table()
    {
        var feed = XlsxParser.Parse(Xlsx(XlsxHeader, Row("1 rue X"), Row("", postalCode: ""), Row("3 rue X")), FileNameTime);

        Assert.Equal(["1 rue X", "3 rue X"], feed.Stations.Select(s => s.Address));
        Assert.Single(feed.Warnings);
    }

    [Fact]
    public void Placeholder_coordinates_become_null()
    {
        var station = Assert.Single(XlsxParser.Parse(Xlsx(XlsxHeader, Row("1 rue X", latitude: 0.0, longitude: 0.0)), FileNameTime).Stations);

        Assert.Null(station.Latitude);
        Assert.Null(station.Longitude);
    }

    public static TheoryData<string, byte[]> NotStationWorkbooks => new()
    {
        { "not a zip", "<html>Not found</html>"u8.ToArray() },
        { "missing column", Xlsx(XlsxHeader.Where(h => h != "Code Postal").ToArray<object?>(), ["Station"]) },
        { "no fuel columns", Xlsx(XlsxHeader.Where(h => !h.StartsWith("Prix", StringComparison.Ordinal)).ToArray<object?>(), ["Station"]) },
        { "no stations", Xlsx(XlsxHeader, [], ["Total de stations : 0"]) },
        { "empty sheet", Xlsx() },
    };

    [Theory]
    [MemberData(nameof(NotStationWorkbooks))]
    public void Rejects_files_that_are_not_station_workbooks(string reason, byte[] xlsx)
    {
        Assert.NotEmpty(reason);
        Assert.Throws<FeedFormatException>(() => XlsxParser.Parse(xlsx, FileNameTime));
    }

    private static object?[] Row(
        string address, string postalCode = "H0H 0H0", string regular = "199.4¢", double latitude = 45.5, double longitude = -73.5) =>
        ["Station", "Shell", address, "Montréal", postalCode, latitude, longitude, regular, "N/D", ""];
}
