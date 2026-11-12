using Essence.Data.Entities;

namespace Essence.Ingest.Hours;

/// <summary>What a station's row of one source says, compared to tell whether it changed.</summary>
public sealed record HoursRow(
    string? OpeningHours,
    string SourceRef,
    string? SourceUrl,
    string MatchedBy,
    int? MatchDistanceM,
    bool Confirmed,
    bool TemporarilyClosed,
    DateTimeOffset? SourceCheckedAt)
{
    public static HoursRow Of(HoursMatch match) => new(
        match.Record.Rule,
        match.Record.Ref,
        match.Record.Link,
        match.MatchedBy,
        match.DistanceM,
        match.Confirmed,
        match.Record.TemporarilyClosed,
        // PostgreSQL's timestamptz takes UTC only.
        match.Record.CheckedAt?.ToUniversalTime());

    public static HoursRow Of(StationHoursEntry entry) => new(
        entry.OpeningHours,
        entry.SourceRef,
        entry.SourceUrl,
        entry.MatchedBy,
        entry.MatchDistanceM,
        entry.Confirmed,
        entry.TemporarilyClosed,
        entry.SourceCheckedAt);

    public void CopyTo(StationHoursEntry entry)
    {
        entry.OpeningHours = OpeningHours;
        entry.SourceRef = SourceRef;
        entry.SourceUrl = SourceUrl;
        entry.MatchedBy = MatchedBy;
        entry.MatchDistanceM = MatchDistanceM;
        entry.Confirmed = Confirmed;
        entry.TemporarilyClosed = TemporarilyClosed;
        entry.SourceCheckedAt = SourceCheckedAt;
    }
}

/// <summary>A source's rows to add, change and remove, by station.</summary>
public sealed record HoursChanges(
    IReadOnlyDictionary<long, HoursRow> Added,
    IReadOnlyDictionary<long, HoursRow> Updated,
    IReadOnlyList<long> Removed)
{
    public int Count => Added.Count + Updated.Count + Removed.Count;
}

public static class HoursDiff
{
    /// <summary>
    /// The rows a run wants against those of its source in the database: a station with no row gets one, a row that says
    /// something else is updated, and a station the run didn't match loses its row.
    /// </summary>
    public static HoursChanges Compute(IReadOnlyDictionary<long, HoursRow> existing, IReadOnlyDictionary<long, HoursRow> wanted)
    {
        var added = new Dictionary<long, HoursRow>();
        var updated = new Dictionary<long, HoursRow>();
        foreach (var (stationId, row) in wanted)
        {
            if (!existing.TryGetValue(stationId, out var current))
            {
                added[stationId] = row;
            }
            else if (current != row)
            {
                updated[stationId] = row;
            }
        }

        var removed = existing.Keys.Where(id => !wanted.ContainsKey(id)).Order().ToList();
        return new HoursChanges(added, updated, removed);
    }
}
