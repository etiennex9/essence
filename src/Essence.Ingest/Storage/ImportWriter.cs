using Essence.Data;
using Essence.Data.Entities;
using Essence.Ingest.Diffing;
using Essence.Ingest.Feed;
using Microsoft.EntityFrameworkCore;

namespace Essence.Ingest.Storage;

/// <summary>What the worker needs from <c>imports</c> at startup.</summary>
public sealed record IngestState(FeedVersion? LastImported, IReadOnlyList<double> RecentDelaysOldestFirst);

/// <summary>Result of <see cref="ImportWriter.ImportAsync"/>. Both are null when another worker already imported the version.</summary>
public sealed record ImportOutcome(Import? Import, ChangeSet? Changes)
{
    public bool AlreadyImported => Import is null;
}

/// <summary>Database side of ingestion: loads the current state, applies a <see cref="ChangeSet"/>, records imports.</summary>
public sealed class ImportWriter(EssenceDbContext db)
{
    /// <summary>Arbitrary key shared by every ingest process ("Essence" in ASCII): imports never run concurrently.</summary>
    private const long AdvisoryLockKey = 0x45_73_73_65_6E_63_65;

    private const int MaxErrorLength = 4000;

    /// <summary>
    /// Status of a new station from a file without a status column (the xlsx): the only value the feed has ever shown.
    /// If it's wrong, the next JSON import corrects it and logs the change.
    /// </summary>
    private const string AssumedStatus = "En opération";

    public async Task<IngestState> LoadStateAsync(int delayHistorySize, CancellationToken cancellationToken)
    {
        var succeeded = db.Imports.AsNoTracking().Where(i => i.Status == ImportStatus.Succeeded);
        var last = await succeeded
            .OrderByDescending(i => i.GeneratedAt)
            .Select(i => new FeedVersion(i.GeneratedAt, i.Sha256, i.Etag))
            .FirstOrDefaultAsync(cancellationToken);
        var delays = await succeeded
            .Where(i => i.DelaySeconds != null)
            .OrderByDescending(i => i.GeneratedAt)
            .Take(delayHistorySize)
            .Select(i => i.DelaySeconds!.Value)
            .ToListAsync(cancellationToken);
        delays.Reverse();
        return new IngestState(last, delays);
    }

    public Task<int> CountFailuresAsync(string sha256, CancellationToken cancellationToken) =>
        db.Imports.CountAsync(i => i.Sha256 == sha256 && i.Status == ImportStatus.Failed, cancellationToken);

    /// <summary>
    /// Diffs the candidate against the stored state and writes only the changes, in one transaction.
    /// The effective time of every change is the file's generated_at.
    /// </summary>
    public async Task<ImportOutcome> ImportAsync(FeedCandidate candidate, string? archivePath, CancellationToken cancellationToken)
    {
        var generatedAt = candidate.GeneratedAt;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);

        if (await db.Imports.AnyAsync(i => i.Status == ImportStatus.Succeeded && i.GeneratedAt >= generatedAt, cancellationToken))
        {
            return new ImportOutcome(null, null);
        }

        var stations = await db.Stations.ToListAsync(cancellationToken);
        var currentPrices = await db.CurrentPrices.ToListAsync(cancellationToken);
        var fuelTypes = await db.FuelTypes.ToDictionaryAsync(f => f.Code, StringComparer.Ordinal, cancellationToken);
        var fuelCodes = fuelTypes.Values.ToDictionary(f => f.Id, f => f.Code);

        var changes = FeedDiffer.Diff(
            stations.Select(ToSnapshot).ToList(),
            currentPrices.Select(p => new PriceSnapshot(p.StationId, fuelCodes[p.FuelTypeId], p.PriceCents, p.IsAvailable, p.PriceHistoryId)).ToList(),
            candidate.Document);

        var import = new Import
        {
            FetchedAt = candidate.FetchedAt,
            GeneratedAt = generatedAt,
            Source = candidate.Source,
            Etag = candidate.ETag,
            Sha256 = candidate.Sha256,
            ArchivePath = archivePath,
            DelaySeconds = candidate.DelaySeconds,
            StationsTotal = changes.StationsTotal,
            StationsAdded = changes.NewStations.Count,
            StationsUpdated = changes.UpdatedStations.Count,
            StationsDeactivated = changes.DeactivatedStationIds.Count,
            PricesChanged = changes.PricesChangedCount,
            Warnings = candidate.Document.Warnings.Count + changes.Warnings.Count,
            Status = ImportStatus.Succeeded,
        };
        db.Imports.Add(import);

        var stationsById = stations.ToDictionary(s => s.Id);
        var stationsByKey = stations.ToDictionary(s => s.NaturalKey, StringComparer.Ordinal);

        foreach (var added in changes.NewStations)
        {
            var station = new Station
            {
                NaturalKey = added.NaturalKey,
                Status = added.Status ?? AssumedStatus,
                Address = added.Address,
                PostalCode = added.PostalCode,
                Region = added.Region,
                IsActive = true,
                FirstSeenAt = generatedAt,
                LastSeenAt = generatedAt,
                UpdatedAt = generatedAt,
            };
            CopyFields(added, station);
            db.Stations.Add(station);
            stationsByKey[added.NaturalKey] = station;
            AddChange(station, StationChange.Created, null, added.NaturalKey);
        }

        foreach (var update in changes.UpdatedStations)
        {
            var station = stationsById[update.StationId];
            CopyFields(update.Station, station);
            station.IsActive = true;
            station.UpdatedAt = generatedAt;
            foreach (var change in update.Changes)
            {
                AddChange(station, change.Field, change.OldValue, change.NewValue);
            }
        }

        foreach (var stationId in changes.DeactivatedStationIds)
        {
            var station = stationsById[stationId];
            station.IsActive = false;
            station.UpdatedAt = generatedAt;
            AddChange(station, StationChange.IsActive, "true", "false");
        }

        var currentByKey = currentPrices.ToDictionary(p => (p.StationId, p.FuelTypeId));
        var historyToClose = new List<long>();

        foreach (var closure in changes.ClosedPrices)
        {
            historyToClose.Add(closure.PriceHistoryId);
            db.CurrentPrices.Remove(currentByKey[(closure.StationId, fuelTypes[closure.FuelType].Id)]);
        }

        foreach (var change in changes.PriceChanges)
        {
            var station = stationsByKey[change.NaturalKey];
            var fuelType = GetOrAddFuelType(change.FuelType);
            var history = new PriceHistory
            {
                Station = station,
                FuelType = fuelType,
                PriceCents = change.PriceCents,
                IsAvailable = change.IsAvailable,
                ValidFrom = generatedAt,
                Import = import,
            };
            db.PriceHistory.Add(history);

            if (change.PreviousHistoryId is { } previousId)
            {
                historyToClose.Add(previousId);
                var current = currentByKey[(station.Id, fuelType.Id)];
                current.PriceCents = change.PriceCents;
                current.IsAvailable = change.IsAvailable;
                current.Since = generatedAt;
                current.PriceHistory = history;
            }
            else
            {
                db.CurrentPrices.Add(new CurrentPrice
                {
                    Station = station,
                    FuelType = fuelType,
                    PriceCents = change.PriceCents,
                    IsAvailable = change.IsAvailable,
                    Since = generatedAt,
                    PriceHistory = history,
                });
            }
        }

        // Close replaced rows before inserting their successors (one open row per station and fuel).
        if (historyToClose.Count > 0)
        {
            await db.PriceHistory
                .Where(p => historyToClose.Contains(p.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ValidTo, (DateTimeOffset?)generatedAt), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Every active station is in this file.
        await db.Stations
            .Where(s => s.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, generatedAt), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new ImportOutcome(import, changes);

        void AddChange(Station station, string field, string? oldValue, string? newValue) =>
            db.StationChanges.Add(new StationChange
            {
                Station = station,
                Import = import,
                Field = field,
                OldValue = oldValue,
                NewValue = newValue,
                ChangedAt = generatedAt,
            });

        FuelType GetOrAddFuelType(string code)
        {
            if (!fuelTypes.TryGetValue(code, out var fuelType))
            {
                fuelType = new FuelType { Code = code };
                db.FuelTypes.Add(fuelType);
                fuelTypes[code] = fuelType;
            }

            return fuelType;
        }
    }

    /// <summary>Records a failed attempt. Call on a fresh context: the failed one may hold half-applied changes.</summary>
    public async Task LogFailureAsync(FeedCandidate candidate, string? archivePath, Exception error, CancellationToken cancellationToken)
    {
        var message = error.ToString();
        db.Imports.Add(new Import
        {
            FetchedAt = candidate.FetchedAt,
            GeneratedAt = candidate.GeneratedAt,
            Source = candidate.Source,
            Etag = candidate.ETag,
            Sha256 = candidate.Sha256,
            ArchivePath = archivePath,
            DelaySeconds = candidate.DelaySeconds,
            StationsTotal = candidate.Document.Stations.Count,
            Warnings = candidate.Document.Warnings.Count,
            Status = ImportStatus.Failed,
            Error = message.Length > MaxErrorLength ? message[..MaxErrorLength] : message,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static StationSnapshot ToSnapshot(Station s) =>
        new(s.Id, s.NaturalKey, s.Name, s.Brand, s.Status, s.Address, s.PostalCode, s.Region, s.Latitude, s.Longitude, s.IsActive);

    private static void CopyFields(FeedStation source, Station target)
    {
        target.Name = source.Name;
        target.Brand = source.Brand;
        target.Status = source.Status ?? target.Status;
        target.Address = source.Address;
        target.PostalCode = source.PostalCode;
        target.Region = source.Region;
        target.Latitude = source.Latitude;
        target.Longitude = source.Longitude;
    }
}
