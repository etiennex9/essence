namespace Essence.Data.Entities;

public enum HoursImportStatus
{
    /// <summary>Fetched, matched and written: some stations' hours changed.</summary>
    Succeeded,

    /// <summary>Fetched and matched, and nothing changed. A success all the same: the source answered with good data.</summary>
    Unchanged,

    /// <summary>
    /// Matched again from the last download kept on disk, because the feed's stations changed, and some stations' hours
    /// changed. Nothing was fetched, so it says nothing about the source: it never counts as a success.
    /// </summary>
    Rematched,

    /// <summary>The download looked wrong (no records, or far fewer stations matched than last time): nothing was written.</summary>
    Rejected,

    /// <summary>The download or the write failed: nothing was written.</summary>
    Failed,

    /// <summary>
    /// The source was found turned off (<c>Hours:&lt;Source&gt;:Enabled</c>), or gone from the image, on a start: its rows
    /// were removed, since nothing refreshes them any more. Its runs before this say nothing about it now: it isn't listed
    /// or checked until it runs again, and then it starts over, as a new source.
    /// </summary>
    Disabled,
}

/// <summary>
/// One run of an hours source (<c>hours_imports</c>), with its counts. Kept apart from <see cref="Import"/>: the feed's
/// readers (the ingest's state, the API's data version and health) assume every succeeded import is a feed version.
/// </summary>
public sealed class HoursImport
{
    public long Id { get; set; }

    /// <summary>The source's name, as in <see cref="StationHoursEntry.Source"/>: <c>costco</c>, <c>curated</c>…</summary>
    public required string Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public HoursImportStatus Status { get; set; }

    /// <summary>Lowercase hex SHA-256 of what the source gave; null when the fetch itself failed.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>The records the source gave that could be read.</summary>
    public int Records { get; set; }

    /// <summary>
    /// The stations those records matched, but for those matched by location only
    /// (<see cref="StationHoursEntry.ByLocation"/>), which get no hours: what the next runs are checked against.
    /// </summary>
    public int Matched { get; set; }

    /// <summary>Rows of <c>station_hours</c> added, updated or removed.</summary>
    public int Changed { get; set; }

    /// <summary>Records left out because their hours couldn't be read for sure.</summary>
    public int Warnings { get; set; }

    public string? Error { get; set; }
}
