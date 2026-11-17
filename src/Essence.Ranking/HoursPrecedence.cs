using Essence.Hours;

namespace Essence.Ranking;

/// <summary>One source's hours for a station, as the ingest stored them (<c>station_hours</c>).</summary>
/// <param name="Source">The source's name: <see cref="HoursPrecedence.Costco"/>, <see cref="HoursPrecedence.Curated"/>…</param>
/// <param name="Rule">The canonical opening_hours; null when the source has the place but not its hours.</param>
/// <param name="Confirmed">The source's place is surely this station.</param>
/// <param name="TemporarilyClosed">The source says the station is closed for now, whatever its hours.</param>
/// <param name="CheckedAt">When the source says the hours were last checked, if it says.</param>
/// <param name="Link">Where a person can check the hours.</param>
public sealed record HoursRow(
    long StationId,
    string Source,
    string? Rule,
    bool Confirmed,
    bool TemporarilyClosed,
    DateTimeOffset? CheckedAt,
    string? Link);

/// <summary>The hours a station shows: the row that won, read.</summary>
/// <param name="Trusted">
/// Sure enough to leave the station out of a recommendation when it's closed: a brand's or Essence's hours, from a source
/// that isn't stale (<see cref="HoursPrecedence.IsStale"/>).
/// </param>
public sealed record ChosenHours(HoursRow Row, OpeningHours Hours, bool Trusted);

/// <summary>
/// Which source's hours a station shows, when several have them: a brand's own locator (Costco's first, its gas bars
/// being the ones the owner checked), then the hours written down by hand in Essence (<c>curated-hours.json</c>), then
/// Couche-Tard's, Ultramar's and Shell's, then OpenStreetMap's, confirmed before not. A source with no success within
/// <see cref="DefaultStaleAfter"/> is stale: its hours come after every fresh source's, so a stale Costco falls behind the
/// curated rule, and they're never trusted. The curated rules ship with the ingest's image, so they're never stale. A row
/// without a rule, or with one that can't be read for sure, doesn't count. The ingest stores canonical rules, but they're
/// read leniently all the same (a "," between rules, "6:00"), since the API serves each again as <c>ToRule</c> writes it.
/// </summary>
public static class HoursPrecedence
{
    public const string Costco = "costco";
    public const string Curated = "curated";
    public const string CircleK = "circlek";
    public const string Ultramar = "ultramar";
    public const string Shell = "shell";
    public const string Osm = "osm";

    /// <summary>As the API's <c>hours</c> health check: <c>Health:HoursStaleAfterDays</c>.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(21);

    /// <summary>The sources by rank, best first; OpenStreetMap's place is ranked by whether it's confirmed (<see cref="Rank"/>).</summary>
    private static readonly string[] Ranked = [Costco, Curated, CircleK, Ultramar, Shell];

    /// <summary>Ranks past every fresh source's: OpenStreetMap confirmed and not, then a source this doesn't know.</summary>
    private static readonly int StaleRank = Ranked.Length + 3;

    /// <summary>The hours each station shows, by station; a station whose rows all lack a rule that can be read isn't in it.</summary>
    /// <param name="lastSuccessBySource">When each source last succeeded; one missing never did, and is stale.</param>
    /// <param name="staleAfter">Without a success for longer, a source is stale; <see cref="DefaultStaleAfter"/> by default.</param>
    public static IReadOnlyDictionary<long, ChosenHours> Choose(
        IEnumerable<HoursRow> rows,
        IReadOnlyDictionary<string, DateTimeOffset> lastSuccessBySource,
        DateTimeOffset now,
        TimeSpan? staleAfter = null)
    {
        var after = staleAfter ?? DefaultStaleAfter;

        // Most stations of a brand share one rule: each is read once.
        var parsed = new Dictionary<string, OpeningHours?>(StringComparer.Ordinal);
        var chosen = new Dictionary<long, (ChosenHours Hours, int Rank)>();
        foreach (var row in rows)
        {
            if (row.Rule is not { } rule)
            {
                continue;
            }

            if (!parsed.TryGetValue(rule, out var hours))
            {
                parsed[rule] = hours = OpeningHours.TryParse(rule, lenient: true, out var read, out _) ? read : null;
            }

            if (hours is null)
            {
                continue;
            }

            var stale = IsStale(row.Source, lastSuccessBySource, now, after);
            var rank = Rank(row) + (stale ? StaleRank : 0);

            // Two sources this doesn't know rank alike: the first by name, so the choice doesn't depend on the rows' order.
            if (chosen.TryGetValue(row.StationId, out var best)
                && (best.Rank < rank || (best.Rank == rank && string.CompareOrdinal(best.Hours.Row.Source, row.Source) <= 0)))
            {
                continue;
            }

            chosen[row.StationId] = (new ChosenHours(row, hours, !stale && IsBrandOrCurated(row.Source)), rank);
        }

        return chosen.ToDictionary(c => c.Key, c => c.Value.Hours);
    }

    /// <summary>No success within <paramref name="staleAfter"/> (or never): the curated rules never are.</summary>
    public static bool IsStale(
        string source, IReadOnlyDictionary<string, DateTimeOffset> lastSuccessBySource, DateTimeOffset now, TimeSpan staleAfter) =>
        source != Curated && (!lastSuccessBySource.TryGetValue(source, out var at) || now - at > staleAfter);

    /// <summary>
    /// When the first source that's fresh at <paramref name="now"/> goes stale, which changes what <see cref="Choose"/>
    /// gives; null when none will.
    /// </summary>
    public static DateTimeOffset? StaleAt(
        IReadOnlyDictionary<string, DateTimeOffset> lastSuccessBySource, DateTimeOffset now, TimeSpan? staleAfter = null)
    {
        var after = staleAfter ?? DefaultStaleAfter;
        DateTimeOffset? first = null;
        foreach (var (source, at) in lastSuccessBySource)
        {
            if (source != Curated && at + after >= now && (first is null || at + after < first))
            {
                first = at + after;
            }
        }

        return first;
    }

    /// <summary>
    /// When the latest source to go stale by <paramref name="now"/> did (its last success and <paramref name="staleAfter"/>),
    /// which changed what <see cref="Choose"/> gives with no run; null when none has.
    /// </summary>
    public static DateTimeOffset? StaleSince(
        IReadOnlyDictionary<string, DateTimeOffset> lastSuccessBySource, DateTimeOffset now, TimeSpan? staleAfter = null)
    {
        var after = staleAfter ?? DefaultStaleAfter;
        DateTimeOffset? latest = null;
        foreach (var (source, at) in lastSuccessBySource)
        {
            if (source != Curated && at + after < now && !(latest >= at + after))
            {
                latest = at + after;
            }
        }

        return latest;
    }

    private static int Rank(HoursRow row) =>
        Array.IndexOf(Ranked, row.Source) is >= 0 and var rank ? rank
        : row.Source == Osm ? Ranked.Length + (row.Confirmed ? 0 : 1)
        : Ranked.Length + 2;

    private static bool IsBrandOrCurated(string source) => Array.IndexOf(Ranked, source) >= 0;
}
