namespace Essence.Ranking;

/// <summary>A set of station ids, kept small (a sorted array, 8 bytes an id): the trends cache one per area.</summary>
public sealed class StationSet
{
    public static StationSet Empty { get; } = new([]);

    private readonly long[] _ids;

    private StationSet(long[] ids) => _ids = ids;

    public static StationSet Of(IEnumerable<long> ids)
    {
        var sorted = ids.Distinct().ToArray();
        Array.Sort(sorted);
        return new StationSet(sorted);
    }

    public int Count => _ids.Length;

    public IReadOnlyList<long> Ids => _ids;

    public bool Contains(long id) => Array.BinarySearch(_ids, id) >= 0;
}
