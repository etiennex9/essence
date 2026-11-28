namespace Essence.Data.Entities;

/// <summary>
/// How an hours source ranks and is named (<c>hours_sources</c>), as the ingest last ran it: one row per source, written on
/// each start for those turned on. The API ranks the sources' rows by it and serves the label, so a source added as a
/// plugin needs no change there; a source without a row ranks as it always did (<c>HoursPrecedence.BuiltIn</c>).
/// </summary>
public sealed class HoursSourceEntry
{
    /// <summary>The source's name, as in <see cref="StationHoursEntry.Source"/>: <c>costco</c>, <c>crevier</c>…</summary>
    public required string Source { get; set; }

    /// <summary>How the app names it: "Couche-Tard", "Crevier".</summary>
    public required string Label { get; set; }

    /// <summary><c>brand</c>, <c>curated</c>, <c>osm</c> or <c>open-data</c>: whether its hours are trusted.</summary>
    public required string Kind { get; set; }

    /// <summary>Lower first: Costco's 10, a brand's plugin 50 by default, OpenStreetMap 60.</summary>
    public int Rank { get; set; }

    /// <summary>
    /// A plugin's: the fingerprint of the spec its last good run was made under. A run under another is the owner's change,
    /// gated as a new source's (<c>HoursGate.Baseline</c>). Null for a source in code, and before a plugin's first good run.
    /// </summary>
    public string? GoodSpec { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
