using Essence.Data.Entities;
using Essence.Ingest.Diffing;
using Essence.Ingest.Feed;
using static Essence.Ingest.Tests.TestFeeds;

namespace Essence.Ingest.Tests;

public sealed class FeedDifferTests
{
    private const string Address = "500 boul. René-Lévesque Ouest, Montréal";

    private static StationSnapshot Stored(FeedStation station, long id = 1, bool isActive = true) =>
        new(id, station.NaturalKey, station.Name, station.Brand, station.Status ?? "En opération", station.Address, station.PostalCode,
            station.Region, station.Latitude, station.Longitude, isActive);

    private static PriceSnapshot StoredPrice(string fuelType, decimal? cents, bool? isAvailable = null, long stationId = 1, long historyId = 100) =>
        new(stationId, fuelType, cents, isAvailable ?? cents is not null, historyId);

    [Fact]
    public void A_new_station_is_added_with_all_its_prices()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m), Price("Diesel", null)]);

        var changes = FeedDiffer.Diff([], [], Document(station));

        Assert.Equal([station], changes.NewStations);
        Assert.Equal(
            [
                new PriceChange(station.NaturalKey, null, "Régulier", 199.4m, true, null),
                new PriceChange(station.NaturalKey, null, "Diesel", null, false, null),
            ],
            changes.PriceChanges);
        Assert.Empty(changes.UpdatedStations);
        Assert.Empty(changes.DeactivatedStationIds);
        Assert.Empty(changes.ClosedPrices);
        Assert.Equal(1, changes.StationsTotal);
    }

    [Fact]
    public void An_unchanged_station_produces_no_changes()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m), Price("Super", null)]);

        var changes = FeedDiffer.Diff(
            [Stored(station)],
            [StoredPrice("Régulier", 199.40m), StoredPrice("Super", null, historyId: 101)],
            Document(station));

        Assert.Empty(changes.NewStations);
        Assert.Empty(changes.UpdatedStations);
        Assert.Empty(changes.DeactivatedStationIds);
        Assert.Empty(changes.PriceChanges);
        Assert.Empty(changes.ClosedPrices);
        Assert.Equal(0, changes.PricesChangedCount);
    }

    [Fact]
    public void A_price_change_replaces_the_open_history_row()
    {
        var station = Station(Address, prices: [Price("Régulier", 201.9m)]);

        var changes = FeedDiffer.Diff([Stored(station)], [StoredPrice("Régulier", 199.4m)], Document(station));

        Assert.Equal([new PriceChange(station.NaturalKey, 1, "Régulier", 201.9m, true, 100)], changes.PriceChanges);
        Assert.Empty(changes.ClosedPrices);
        Assert.Empty(changes.UpdatedStations);
    }

    [Fact]
    public void An_availability_change_is_a_price_change()
    {
        var station = Station(Address, prices: [Price("Diesel", null, isAvailable: false)]);

        var changes = FeedDiffer.Diff([Stored(station)], [StoredPrice("Diesel", 289.9m)], Document(station));

        Assert.Equal([new PriceChange(station.NaturalKey, 1, "Diesel", null, false, 100)], changes.PriceChanges);
    }

    [Fact]
    public void A_brand_change_updates_the_station_and_logs_the_field()
    {
        var before = Station(Address, brand: "Esso");
        var after = Station(Address, brand: "Shell");

        var changes = FeedDiffer.Diff([Stored(before)], [StoredPrice("Régulier", 199.4m)], Document(after));

        var update = Assert.Single(changes.UpdatedStations);
        Assert.Equal(1, update.StationId);
        Assert.False(update.Reactivated);
        Assert.Equal([new FieldChange("brand", "Esso", "Shell")], update.Changes);
        Assert.Empty(changes.PriceChanges);
    }

    [Fact]
    public void Coordinate_changes_are_logged_as_text()
    {
        var before = Station(Address);
        var after = before with { Latitude = 45.50171, Longitude = null };

        var changes = FeedDiffer.Diff([Stored(before)], [StoredPrice("Régulier", 199.4m)], Document(after));

        Assert.Equal(
            [new FieldChange("latitude", "45.5", "45.50171"), new FieldChange("longitude", "-73.5", null)],
            Assert.Single(changes.UpdatedStations).Changes);
    }

    [Fact]
    public void A_missing_station_is_deactivated_and_its_prices_closed()
    {
        var present = Station(Address);
        var missing = Station("1 rue Ailleurs, Québec");

        var changes = FeedDiffer.Diff(
            [Stored(present, id: 1), Stored(missing, id: 2)],
            [
                StoredPrice("Régulier", 199.4m, stationId: 1, historyId: 100),
                StoredPrice("Régulier", 205.9m, stationId: 2, historyId: 200),
                StoredPrice("Diesel", 289.9m, stationId: 2, historyId: 201),
            ],
            Document(present));

        Assert.Equal([2L], changes.DeactivatedStationIds);
        Assert.Equal(
            [new PriceClosure(2, "Régulier", 200), new PriceClosure(2, "Diesel", 201)],
            changes.ClosedPrices);
        Assert.Empty(changes.PriceChanges);
    }

    [Fact]
    public void An_already_inactive_station_stays_untouched()
    {
        var present = Station(Address);
        var gone = Station("1 rue Ailleurs, Québec");

        var changes = FeedDiffer.Diff(
            [Stored(present, id: 1), Stored(gone, id: 2, isActive: false)],
            [StoredPrice("Régulier", 199.4m, stationId: 1)],
            Document(present));

        Assert.Empty(changes.DeactivatedStationIds);
        Assert.Empty(changes.ClosedPrices);
    }

    [Fact]
    public void A_station_that_reappears_is_reactivated_with_fresh_prices()
    {
        var station = Station(Address, brand: "Ultramar", prices: [Price("Régulier", 198.9m)]);
        var stored = Stored(station with { Brand = "Esso" }, isActive: false);

        var changes = FeedDiffer.Diff([stored], [], Document(station));

        var update = Assert.Single(changes.UpdatedStations);
        Assert.True(update.Reactivated);
        Assert.Equal(
            [new FieldChange(StationChange.IsActive, "false", "true"), new FieldChange("brand", "Esso", "Ultramar")],
            update.Changes);
        Assert.Equal([new PriceChange(station.NaturalKey, 1, "Régulier", 198.9m, true, null)], changes.PriceChanges);
        Assert.Equal(1, changes.ReactivatedCount);
        Assert.Empty(changes.NewStations);
    }

    [Fact]
    public void A_dropped_fuel_closes_its_price()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m)]);

        var changes = FeedDiffer.Diff(
            [Stored(station)],
            [StoredPrice("Régulier", 199.4m, historyId: 100), StoredPrice("Diesel", 289.9m, historyId: 101)],
            Document(station));

        Assert.Equal([new PriceClosure(1, "Diesel", 101)], changes.ClosedPrices);
        Assert.Empty(changes.PriceChanges);
        Assert.Empty(changes.DeactivatedStationIds);
    }

    [Fact]
    public void A_skipped_unparsable_price_keeps_its_current_price()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m)], skippedFuelTypes: ["Diesel"]);

        var changes = FeedDiffer.Diff(
            [Stored(station)],
            [StoredPrice("Régulier", 199.4m, historyId: 100), StoredPrice("Diesel", 289.9m, historyId: 101)],
            Document(station));

        Assert.Empty(changes.ClosedPrices);
        Assert.Empty(changes.PriceChanges);
    }

    [Fact]
    public void A_file_without_status_keeps_the_stored_one()
    {
        var stored = Station(Address, status: "Fermé temporairement");
        var xlsx = Station(Address, status: null);

        var changes = FeedDiffer.Diff([Stored(stored)], [StoredPrice("Régulier", 199.4m)], Document(xlsx));

        Assert.Empty(changes.UpdatedStations);
        Assert.Empty(changes.PriceChanges);
    }

    [Fact]
    public void A_status_change_is_logged()
    {
        var before = Station(Address, status: "En opération");
        var after = Station(Address, status: "Fermé temporairement");

        var changes = FeedDiffer.Diff([Stored(before)], [StoredPrice("Régulier", 199.4m)], Document(after));

        Assert.Equal([new FieldChange("status", "En opération", "Fermé temporairement")], Assert.Single(changes.UpdatedStations).Changes);
    }

    [Fact]
    public void Not_available_on_a_fuel_the_station_sells_makes_it_unavailable()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m)], notAvailableFuelTypes: ["Diesel"]);

        var changes = FeedDiffer.Diff(
            [Stored(station)],
            [StoredPrice("Régulier", 199.4m, historyId: 100), StoredPrice("Diesel", 289.9m, historyId: 101)],
            Document(station));

        Assert.Equal([new PriceChange(station.NaturalKey, 1, "Diesel", null, false, 101)], changes.PriceChanges);
        Assert.Empty(changes.ClosedPrices);
    }

    [Fact]
    public void Not_available_on_an_already_unavailable_fuel_changes_nothing()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m)], notAvailableFuelTypes: ["Diesel"]);

        var changes = FeedDiffer.Diff(
            [Stored(station)],
            [StoredPrice("Régulier", 199.4m, historyId: 100), StoredPrice("Diesel", null, historyId: 101)],
            Document(station));

        Assert.Empty(changes.PriceChanges);
        Assert.Empty(changes.ClosedPrices);
    }

    [Fact]
    public void Not_available_on_a_fuel_the_station_doesnt_sell_is_ignored()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m)], notAvailableFuelTypes: ["Super", "Diesel"]);

        var changes = FeedDiffer.Diff([Stored(station)], [StoredPrice("Régulier", 199.4m)], Document(station));

        Assert.Empty(changes.PriceChanges);
        Assert.Empty(changes.ClosedPrices);
    }

    [Fact]
    public void Not_available_on_a_new_station_adds_no_price()
    {
        var station = Station(Address, prices: [Price("Régulier", 199.4m)], notAvailableFuelTypes: ["Diesel"]);

        var changes = FeedDiffer.Diff([], [], Document(station));

        Assert.Equal([new PriceChange(station.NaturalKey, null, "Régulier", 199.4m, true, null)], changes.PriceChanges);
    }

    [Fact]
    public void A_duplicate_key_keeps_the_first_occurrence_and_warns()
    {
        var first = Station(Address, prices: [Price("Régulier", 199.4m)]);
        var duplicate = Station(Address.ToUpperInvariant(), prices: [Price("Régulier", 150.0m)]);
        Assert.Equal(first.NaturalKey, duplicate.NaturalKey);

        var changes = FeedDiffer.Diff([], [], Document(first, duplicate));

        Assert.Equal([first], changes.NewStations);
        Assert.Equal([new PriceChange(first.NaturalKey, null, "Régulier", 199.4m, true, null)], changes.PriceChanges);
        Assert.Single(changes.Warnings);
        Assert.Equal(1, changes.StationsTotal);
    }

    [Fact]
    public void The_sample_file_against_an_empty_database_adds_everything()
    {
        var feed = FeedParser.Parse(Fixture());

        var changes = FeedDiffer.Diff([], [], feed);

        Assert.Equal(2463, changes.NewStations.Count);
        Assert.Equal(7127, changes.PriceChanges.Count);
        Assert.Empty(changes.Warnings);
    }
}
