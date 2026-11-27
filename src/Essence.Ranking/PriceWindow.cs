namespace Essence.Ranking;

/// <summary>A successful import: its id and the feed's <c>generated_at</c>, which its changes are stamped with.</summary>
public readonly record struct ImportTime(long Id, DateTimeOffset GeneratedAt);

/// <summary>A <c>price_history</c> row as the trends read it.</summary>
/// <param name="ValidTo">Null while it's still the price.</param>
public readonly record struct HistoryRow(
    long Id, long StationId, short FuelTypeId, decimal? PriceCents, bool IsAvailable, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

/// <summary>A row that was open and has been closed since, with no row after it (the fuel stopped, or the station left).</summary>
public readonly record struct ClosedRow(long Id, DateTimeOffset ValidTo);

/// <summary>
/// The recent price history of every station's fuel, in memory (<see cref="TrendRules.Window"/>), and the imports that
/// cover it. Immutable: each new import makes a new window (<see cref="With"/>) that copies the index and rebuilds only the
/// prices that changed, so the requests reading the old one never see half an update.
/// </summary>
public sealed class PriceWindow
{
    public static PriceWindow Empty { get; } = new(
        new Dictionary<(long, short), StationPrices>(), new Dictionary<(long, short), long>(), Coverage.None, null);

    private readonly Dictionary<(long StationId, short FuelTypeId), StationPrices> _prices;

    /// <summary>Each fuel's open row: the price it has now, which a later import may close.</summary>
    private readonly Dictionary<(long StationId, short FuelTypeId), long> _openRowOf;

    private PriceWindow(
        Dictionary<(long, short), StationPrices> prices, Dictionary<(long, short), long> openRowOf, Coverage coverage, ImportTime? lastImport)
    {
        _prices = prices;
        _openRowOf = openRowOf;
        Coverage = coverage;
        LastImport = lastImport;
    }

    public Coverage Coverage { get; }

    /// <summary>The latest import read; null before the first.</summary>
    public ImportTime? LastImport { get; }

    /// <summary>The open rows' ids, to ask which have been closed since.</summary>
    public IReadOnlyCollection<long> OpenRowIds => _openRowOf.Values;

    /// <summary>How many station fuels and price spans it holds, for the log.</summary>
    public (int Fuels, int Spans) Size => (_prices.Count, _prices.Values.Sum(p => p.Count));

    public StationPrices? Of(long stationId, short fuelTypeId) => _prices.GetValueOrDefault((stationId, fuelTypeId));

    /// <summary>
    /// A window with new imports and their rows, the rows closed since without a successor, and what ended before
    /// <paramref name="keepFrom"/> dropped from the fuels that changed (<see cref="Trimmed"/> trims them all). A row
    /// replaces its fuel's open price at its own <c>valid_from</c>. Giving the same rows again changes nothing: a fuel has
    /// one row per import, so a row starting when one already does is known.
    /// </summary>
    /// <param name="rows">In <c>valid_from</c> order, as the queries read them: a row older than its fuel's latest is only slotted in.</param>
    public PriceWindow With(
        IReadOnlyCollection<ImportTime> imports, IEnumerable<HistoryRow> rows, IReadOnlyCollection<ClosedRow> closed, DateTimeOffset keepFrom)
    {
        var keep = keepFrom.UtcTicks;
        var prices = new Dictionary<(long, short), StationPrices>(_prices);
        var openRowOf = new Dictionary<(long, short), long>(_openRowOf);
        var changed = new Dictionary<(long, short), List<PriceSpan>>();

        List<PriceSpan> SpansOf((long, short) key)
        {
            if (!changed.TryGetValue(key, out var spans))
            {
                spans = prices.TryGetValue(key, out var known) ? [.. known.Spans] : [];
                changed[key] = spans;
            }

            return spans;
        }

        if (closed.Count > 0)
        {
            var keyOfOpen = openRowOf.ToDictionary(o => o.Value, o => o.Key);
            foreach (var close in closed)
            {
                if (keyOfOpen.TryGetValue(close.Id, out var key))
                {
                    openRowOf.Remove(key);
                    var spans = SpansOf(key);
                    if (spans.Count > 0 && spans[^1].To == PriceSpan.Open)
                    {
                        spans[^1] = spans[^1] with { To = close.ValidTo.UtcTicks };
                    }
                }
            }
        }

        var comparer = Comparer<PriceSpan>.Create((a, b) => a.From.CompareTo(b.From));
        foreach (var row in rows)
        {
            var key = (row.StationId, row.FuelTypeId);
            var spans = SpansOf(key);
            var span = new PriceSpan(row.ValidFrom.UtcTicks, row.ValidTo?.UtcTicks ?? PriceSpan.Open, PriceSpan.ToTenths(row.PriceCents, row.IsAvailable));
            if (spans.Count > 0 && span.From <= spans[^1].From)
            {
                // Known already, or (never from the feed, whose imports come in order) older than the latest: kept in order.
                var index = spans.BinarySearch(span, comparer);
                if (index < 0)
                {
                    spans.Insert(~index, span);
                }

                continue;
            }

            if (spans.Count > 0 && spans[^1].To > span.From)
            {
                spans[^1] = spans[^1] with { To = span.From };
            }

            spans.Add(span);
            if (row.ValidTo is null)
            {
                openRowOf[key] = row.Id;
            }
            else
            {
                openRowOf.Remove(key);
            }
        }

        foreach (var (key, spans) in changed)
        {
            spans.RemoveAll(s => s.To <= keep);
            if (spans.Count == 0)
            {
                prices.Remove(key);
            }
            else
            {
                prices[key] = new StationPrices([.. spans]);
            }
        }

        var lastImport = LastImport;
        foreach (var import in imports)
        {
            if (lastImport is not { } known || import.GeneratedAt > known.GeneratedAt)
            {
                lastImport = import;
            }
        }

        return new PriceWindow(
            prices, openRowOf, Coverage.With(imports.Select(i => i.GeneratedAt.UtcTicks), keep - TrendRules.Reach.Ticks), lastImport);
    }

    /// <summary>This window without what ended before <paramref name="keepFrom"/>, in every fuel.</summary>
    public PriceWindow Trimmed(DateTimeOffset keepFrom)
    {
        var keep = keepFrom.UtcTicks;
        var prices = new Dictionary<(long, short), StationPrices>(_prices.Count);
        foreach (var (key, known) in _prices)
        {
            if (known.Count > 0 && known[0].To > keep)
            {
                prices[key] = known;
            }
            else if (known.Spans.Where(s => s.To > keep).ToArray() is { Length: > 0 } kept)
            {
                prices[key] = new StationPrices(kept);
            }
        }

        var openRowOf = _openRowOf.Where(o => prices.ContainsKey(o.Key)).ToDictionary(o => o.Key, o => o.Value);
        return new PriceWindow(prices, openRowOf, Coverage.With([], keep - TrendRules.Reach.Ticks), LastImport);
    }
}
