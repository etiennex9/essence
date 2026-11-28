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

/// <summary>What kind of source an hours source is, which says whether its hours are trusted.</summary>
public enum HoursSourceKind
{
    /// <summary>A brand's own locator: trusted while it isn't stale.</summary>
    Brand,

    /// <summary>Rules written down by hand in Essence (<c>curated-hours.json</c>): trusted, and never stale.</summary>
    Curated,

    /// <summary>OpenStreetMap: shown, never trusted; a place not confirmed ranks after one confirmed.</summary>
    Osm,

    /// <summary>Another open dataset: shown, never trusted; a place not confirmed ranks after one confirmed.</summary>
    OpenData,
}

/// <summary>The kinds as stored (<c>hours_sources.kind</c>) and as plugins' specs write them: <c>brand</c>, <c>open-data</c>…</summary>
public static class HoursSourceKinds
{
    public static string Name(HoursSourceKind kind) => kind switch
    {
        HoursSourceKind.Brand => "brand",
        HoursSourceKind.Curated => "curated",
        HoursSourceKind.Osm => "osm",
        HoursSourceKind.OpenData => "open-data",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParse(string? name, out HoursSourceKind kind)
    {
        foreach (var candidate in Enum.GetValues<HoursSourceKind>())
        {
            if (string.Equals(Name(candidate), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>
/// How a source's hours are ranked and shown: what the ingest stores for each source it runs (<c>hours_sources</c>), and
/// what <see cref="HoursPrecedence.BuiltIn"/> gives the sources written in code when it has none.
/// </summary>
/// <param name="Label">How the app names the source: "Couche-Tard", "Crevier".</param>
/// <param name="Rank">Lower first (<see cref="HoursPrecedence"/>).</param>
public sealed record HoursSourceProfile(string Label, HoursSourceKind Kind, int Rank)
{
    /// <summary>The rank of a brand's locator added as a plugin whose spec gives none: after the brands in code, before OpenStreetMap.</summary>
    public const int DefaultBrandRank = 50;

    /// <summary>The rank of an open dataset added as a plugin whose spec gives none: after OpenStreetMap.</summary>
    public const int DefaultOpenDataRank = 80;

    /// <summary>Its hours are sure enough to leave a station that's closed out of a recommendation, while the source isn't stale.</summary>
    public bool Trusted => Kind is HoursSourceKind.Brand or HoursSourceKind.Curated;

    /// <summary>A place not confirmed (<see cref="HoursRow.Confirmed"/>) ranks right after a place of the same source that is.</summary>
    public bool ConfirmedFirst => Kind is HoursSourceKind.Osm or HoursSourceKind.OpenData;
}

/// <summary>
/// Which source's hours a station shows, when several have them: the one of lowest rank (<see cref="HoursSourceProfile.Rank"/>).
/// The sources written in code keep their order (<see cref="BuiltIn"/>): a brand's own locator (Costco's first, its gas bars
/// being the ones the owner checked), then the hours written down by hand in Essence (<c>curated-hours.json</c>), then
/// Ultramar's and Shell's forecourts', then Couche-Tard's, which are its store's (a Couche-Tard that sells Shell's gas
/// shows the pumps' hours), then OpenStreetMap's, confirmed before not. A brand's locator added as a plugin comes after the
/// brands in code and before OpenStreetMap unless its spec says otherwise, another open dataset after OpenStreetMap, and a
/// source this knows nothing of last. A source with no success within <see cref="DefaultStaleAfter"/> is stale: its hours
/// come after every fresh source's, so a stale Costco falls behind the curated rule, and they're never trusted. The curated
/// rules ship with the ingest's image, so they're never stale. A row without a rule, or with one that can't be read for
/// sure, doesn't count. The ingest stores canonical rules, but they're read leniently all the same (a "," between rules,
/// "6:00"), since the API serves each again as <c>ToRule</c> writes it.
/// </summary>
public static class HoursPrecedence
{
    public const string Costco = "costco";
    public const string Curated = "curated";
    public const string CircleK = "circlek";
    public const string Ultramar = "ultramar";
    public const string Shell = "shell";
    public const string Osm = "osm";

    /// <summary>The rank of a source this knows nothing of: after every other.</summary>
    public const int UnknownRank = 90;

    /// <summary>Added to a stale source's rank: past every fresh source's, a place not confirmed included.</summary>
    private const int StaleRank = 1000;

    /// <summary>As the API's <c>hours</c> health check: <c>Health:HoursStaleAfterDays</c>.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(21);

    /// <summary>
    /// The sources written in code, ranked as they always were: what a source is ranked by when the ingest stored no
    /// profile for it (an older database, or before the ingest's first start). A plugin that takes one's place later takes
    /// its name and rank too.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, HoursSourceProfile> BuiltIn = new Dictionary<string, HoursSourceProfile>(StringComparer.Ordinal)
    {
        [Costco] = new("Costco", HoursSourceKind.Brand, 10),
        [Curated] = new("Essence", HoursSourceKind.Curated, 20),
        [Ultramar] = new("Ultramar", HoursSourceKind.Brand, 30),
        [Shell] = new("Shell", HoursSourceKind.Brand, 31),
        [CircleK] = new("Couche-Tard", HoursSourceKind.Brand, 40),
        [Osm] = new("OpenStreetMap", HoursSourceKind.Osm, 60),
    };

    /// <summary>The hours each station shows, by station; a station whose rows all lack a rule that can be read isn't in it.</summary>
    /// <param name="lastSuccessBySource">When each source last succeeded; one missing never did, and is stale.</param>
    /// <param name="staleAfter">Without a success for longer, a source is stale; <see cref="DefaultStaleAfter"/> by default.</param>
    /// <param name="profiles">Each source's profile as the ingest stored it; one missing is <see cref="BuiltIn"/>'s, or unknown.</param>
    public static IReadOnlyDictionary<long, ChosenHours> Choose(
        IEnumerable<HoursRow> rows,
        IReadOnlyDictionary<string, DateTimeOffset> lastSuccessBySource,
        DateTimeOffset now,
        TimeSpan? staleAfter = null,
        IReadOnlyDictionary<string, HoursSourceProfile>? profiles = null)
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
            var profile = ProfileOf(row.Source, profiles);
            var rank = Rank(row, profile) + (stale ? StaleRank : 0);

            // Two sources of the same rank: the first by name, so the choice doesn't depend on the rows' order.
            if (chosen.TryGetValue(row.StationId, out var best)
                && (best.Rank < rank || (best.Rank == rank && string.CompareOrdinal(best.Hours.Row.Source, row.Source) <= 0)))
            {
                continue;
            }

            chosen[row.StationId] = (new ChosenHours(row, hours, !stale && profile is { Trusted: true }), rank);
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

    /// <summary>The source's profile: the one stored, else <see cref="BuiltIn"/>'s; null for a source this knows nothing of.</summary>
    public static HoursSourceProfile? ProfileOf(string source, IReadOnlyDictionary<string, HoursSourceProfile>? profiles = null) =>
        profiles?.GetValueOrDefault(source) ?? BuiltIn.GetValueOrDefault(source);

    private static int Rank(HoursRow row, HoursSourceProfile? profile) =>
        (profile?.Rank ?? UnknownRank) + (profile is { ConfirmedFirst: true } && !row.Confirmed ? 1 : 0);
}
