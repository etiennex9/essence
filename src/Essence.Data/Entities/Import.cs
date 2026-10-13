namespace Essence.Data.Entities;

public enum ImportStatus
{
    Succeeded,
    Failed,
}

/// <summary>Which file an import came from.</summary>
public enum ImportSource
{
    /// <summary>The live <c>stations.geojson.gz</c>.</summary>
    Feed,

    /// <summary>A dated xlsx snapshot, downloaded to backfill a version the worker missed.</summary>
    Xlsx,
}

/// <summary>One attempt to import a new version of the feed, with its statistics.</summary>
public sealed class Import
{
    public long Id { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The feed's <c>metadata.generated_at</c>; unique among succeeded imports.</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    public ImportSource Source { get; set; }

    public string? Etag { get; set; }

    /// <summary>Lowercase hex SHA-256 of the decompressed feed, or of the xlsx file.</summary>
    public required string Sha256 { get; set; }

    public string? ArchivePath { get; set; }

    /// <summary>
    /// Seconds between <see cref="GeneratedAt"/> and <see cref="FetchedAt"/>: how long the file took to reach us.
    /// Null for backfilled xlsx files, which were fetched late on purpose.
    /// </summary>
    public double? DelaySeconds { get; set; }

    public int StationsTotal { get; set; }
    public int StationsAdded { get; set; }
    public int StationsUpdated { get; set; }
    public int StationsDeactivated { get; set; }
    public int PricesChanged { get; set; }
    public int Warnings { get; set; }
    public ImportStatus Status { get; set; }
    public string? Error { get; set; }
}
