using System.Globalization;
using Essence.Data.Entities;
using Essence.Ingest.Feed;

namespace Essence.Ingest.Diffing;

/// <summary>Compares the stored state with a feed file and lists only what changed. No database access.</summary>
public static class FeedDiffer
{
    public static ChangeSet Diff(
        IReadOnlyCollection<StationSnapshot> stations,
        IReadOnlyCollection<PriceSnapshot> currentPrices,
        FeedDocument feed)
    {
        var stationsByKey = stations.ToDictionary(s => s.NaturalKey, StringComparer.Ordinal);
        var pricesByStation = currentPrices
            .GroupBy(p => p.StationId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(p => p.FuelType, StringComparer.Ordinal));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newStations = new List<FeedStation>();
        var updates = new List<StationUpdate>();
        var deactivated = new List<long>();
        var priceChanges = new List<PriceChange>();
        var closures = new List<PriceClosure>();
        var warnings = new List<string>();

        foreach (var station in feed.Stations)
        {
            if (!seen.Add(station.NaturalKey))
            {
                warnings.Add($"Duplicate station key {station.NaturalKey} ({station.Name}); kept the first occurrence.");
                continue;
            }

            if (!stationsByKey.TryGetValue(station.NaturalKey, out var stored))
            {
                newStations.Add(station);
                priceChanges.AddRange(station.Prices.Select(p =>
                    new PriceChange(station.NaturalKey, null, p.FuelType, p.PriceCents, p.IsAvailable, null)));
                continue;
            }

            var changes = CompareFields(stored, station);
            if (!stored.IsActive)
            {
                changes.Insert(0, new FieldChange(StationChange.IsActive, "false", "true"));
            }

            if (changes.Count > 0)
            {
                updates.Add(new StationUpdate(stored.Id, station, changes, Reactivated: !stored.IsActive));
            }

            var current = pricesByStation.GetValueOrDefault(stored.Id);
            var prices = EffectivePrices(station, current);
            foreach (var price in prices)
            {
                PriceSnapshot? previous = null;
                if (current?.TryGetValue(price.FuelType, out previous) == true
                    && previous.PriceCents == price.PriceCents
                    && previous.IsAvailable == price.IsAvailable)
                {
                    continue;
                }

                priceChanges.Add(new PriceChange(
                    station.NaturalKey, stored.Id, price.FuelType, price.PriceCents, price.IsAvailable, previous?.PriceHistoryId));
            }

            if (current is not null)
            {
                closures.AddRange(current.Values
                    .Where(p => !prices.Any(x => x.FuelType == p.FuelType) && !station.SkippedFuelTypes.Contains(p.FuelType))
                    .Select(p => new PriceClosure(stored.Id, p.FuelType, p.PriceHistoryId)));
            }
        }

        foreach (var stored in stations)
        {
            if (stored.IsActive && !seen.Contains(stored.NaturalKey))
            {
                deactivated.Add(stored.Id);
                if (pricesByStation.TryGetValue(stored.Id, out var current))
                {
                    closures.AddRange(current.Values.Select(p => new PriceClosure(stored.Id, p.FuelType, p.PriceHistoryId)));
                }
            }
        }

        return new ChangeSet(seen.Count, newStations, updates, deactivated, priceChanges, closures, warnings);
    }

    private static List<FieldChange> CompareFields(StationSnapshot stored, FeedStation station)
    {
        var changes = new List<FieldChange>();
        Compare("name", stored.Name, station.Name);
        Compare("brand", stored.Brand, station.Brand);
        if (station.Status is not null)
        {
            Compare("status", stored.Status, station.Status);
        }

        Compare("address", stored.Address, station.Address);
        Compare("postal_code", stored.PostalCode, station.PostalCode);
        Compare("region", stored.Region, station.Region);
        Compare("latitude", Format(stored.Latitude), Format(station.Latitude));
        Compare("longitude", Format(stored.Longitude), Format(station.Longitude));
        return changes;

        void Compare(string field, string? oldValue, string? newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(field, oldValue, newValue));
            }
        }
    }

    /// <summary>
    /// The file's prices, plus "unavailable" for each <see cref="FeedStation.NotAvailableFuelTypes"/> fuel the station currently has.
    /// One it doesn't have isn't sold there, so it's left out (and not closed, since it has no price).
    /// </summary>
    private static IReadOnlyList<FeedPrice> EffectivePrices(FeedStation station, Dictionary<string, PriceSnapshot>? current)
    {
        if (station.NotAvailableFuelTypes.Count == 0 || current is null)
        {
            return station.Prices;
        }

        var prices = station.Prices.ToList();
        foreach (var fuelType in station.NotAvailableFuelTypes)
        {
            if (current.ContainsKey(fuelType) && !prices.Exists(p => p.FuelType == fuelType))
            {
                prices.Add(new FeedPrice(fuelType, null, IsAvailable: false));
            }
        }

        return prices;
    }

    private static string? Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);
}
