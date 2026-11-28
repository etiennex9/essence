namespace Essence.Ranking;

/// <summary>
/// Which moments the price history covers: those within <see cref="TrendRules.Reach"/> after a successful import. Between
/// two imports further apart is a gap, where a price may have changed unseen. Times are UTC ticks.
/// </summary>
public sealed class Coverage
{
    public static Coverage None { get; } = new([]);

    private readonly long[] _imports;

    /// <summary>The starts of the gaps: the moment after an import followed by none within <see cref="TrendRules.Reach"/>.</summary>
    private readonly long[] _gapStarts;

    private Coverage(long[] imports)
    {
        _imports = imports;
        var reach = TrendRules.Reach.Ticks;
        var gaps = new List<long>();
        for (var i = 1; i < imports.Length; i++)
        {
            if (imports[i] - imports[i - 1] > reach)
            {
                gaps.Add(imports[i - 1] + reach);
            }
        }

        _gapStarts = [.. gaps];
    }

    /// <summary>The successful imports' <c>generated_at</c>, in any order.</summary>
    public static Coverage Of(IEnumerable<DateTimeOffset> imports) => Of(imports.Select(i => i.UtcTicks));

    public static Coverage Of(IEnumerable<long> imports)
    {
        var sorted = imports.Distinct().ToArray();
        Array.Sort(sorted);
        return new Coverage(sorted);
    }

    public int Count => _imports.Length;

    public long? First => _imports.Length > 0 ? _imports[0] : null;

    public long? Last => _imports.Length > 0 ? _imports[^1] : null;

    /// <summary>This coverage with <paramref name="more"/> imports, those before <paramref name="keepFrom"/> dropped.</summary>
    public Coverage With(IEnumerable<long> more, long keepFrom) => Of(_imports.Concat(more).Where(i => i >= keepFrom));

    /// <summary>The latest import at or before <paramref name="ticks"/>; null when none is.</summary>
    public long? LatestAtOrBefore(long ticks)
    {
        var index = Array.BinarySearch(_imports, ticks);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 ? _imports[index] : null;
    }

    public bool IsCovered(long ticks) => LatestAtOrBefore(ticks) is { } import && ticks - import <= TrendRules.Reach.Ticks;

    /// <summary>
    /// Whether the import at <paramref name="import"/> came after a gap (or is the first one known): the changes it brought
    /// may have happened any time during the gap, so they're never counted as moves at that moment.
    /// </summary>
    public bool IsResume(long import)
    {
        var index = Array.BinarySearch(_imports, import);
        if (index < 0)
        {
            index = ~index;
        }

        return index == 0 || import - _imports[index - 1] > TrendRules.Reach.Ticks;
    }

    /// <summary>Whether some moment of [<paramref name="from"/>, <paramref name="to"/>] is covered.</summary>
    public bool AnyCovered(long from, long to)
    {
        if (to < from)
        {
            return false;
        }

        return LatestAtOrBefore(to) is { } import && (import >= from || from - import <= TrendRules.Reach.Ticks);
    }

    /// <summary>Whether all of [<paramref name="from"/>, <paramref name="to"/>] is covered: no gap starts in it.</summary>
    public bool FullyCovered(long from, long to)
    {
        if (!IsCovered(from) || !IsCovered(to))
        {
            return false;
        }

        var index = Array.BinarySearch(_gapStarts, from);
        if (index < 0)
        {
            index = ~index;
        }

        return index >= _gapStarts.Length || _gapStarts[index] > to;
    }
}
