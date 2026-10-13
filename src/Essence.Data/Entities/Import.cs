namespace Essence.Data.Entities;

public enum ImportStatus
{
    Succeeded,
    Failed,
}

/// <summary>One attempt to import a new version of the feed, with its statistics.</summary>
public sealed class Import
{
    public long Id { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The feed's <c>metadata.generated_at</c>; unique among succeeded imports.</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    public string? Etag { get; set; }

    /// <summary>Lowercase hex SHA-256 of the decompressed feed.</summary>
    public required string Sha256 { get; set; }

    public string? ArchivePath { get; set; }

    /// <summary>Seconds between <see cref="GeneratedAt"/> and <see cref="FetchedAt"/>: how long the file took to reach us.</summary>
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
