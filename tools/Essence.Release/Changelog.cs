using Essence.Contracts;

namespace Essence.Release;

/// <summary>A <c>changelog/*.json</c> file.</summary>
/// <param name="Path">As git lists it: <c>changelog/name.json</c>.</param>
/// <param name="Date">Set only for an entry written after the fact; otherwise the day it landed is used.</param>
public sealed record Entry(string Path, ChangelogEntry Text, DateOnly? Date = null);

/// <summary>A day tag and the changelog files in its tree.</summary>
public sealed record DayTree(DateOnly Day, IReadOnlySet<string> Paths);

/// <summary>release.json and the month files it lists, by month ("2026-10").</summary>
public sealed record Notes(ReleaseInfo Release, IReadOnlyDictionary<string, ChangelogMonth> Months);

public static class Changelog
{
    /// <summary>
    /// Dates each entry by the first day tag whose tree has its file, the day it landed on main. Tags keep their trees, so
    /// this holds after main is rewritten, and whether the file came in a commit of its own or through a merge. An entry in
    /// no tag yet is the build's own change: it gets <paramref name="version"/>, or is unreleased without one. Days after
    /// <paramref name="version"/> are ignored (an older commit rebuilt), and days without entries aren't versions.
    /// </summary>
    public static Notes Build(DateOnly? version, string? commit, IEnumerable<DayTree> days, IEnumerable<Entry> entries)
    {
        var trees = days.Where(d => version is null || d.Day <= version).OrderBy(d => d.Day).ToList();
        var dated = new List<(DateOnly Day, Entry Entry)>();
        var unreleased = new List<Entry>();
        foreach (var entry in entries)
        {
            if ((entry.Date ?? trees.FirstOrDefault(t => t.Paths.Contains(entry.Path))?.Day ?? version) is { } day)
            {
                if (version is null || day <= version)
                {
                    dated.Add((day, entry));
                }
            }
            else
            {
                unreleased.Add(entry);
            }
        }

        var months = dated
            .GroupBy(d => d.Day)
            .Select(g => new ChangelogVersion(g.Key, g.OrderBy(d => d.Entry.Path, StringComparer.Ordinal).Select(d => d.Entry.Text).ToList()))
            .GroupBy(v => Days.Month(v.Date))
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Month: g.Key, Page: new ChangelogMonth(g.OrderByDescending(v => v.Date).ToList())))
            .ToList();

        var release = new ReleaseInfo(
            version,
            commit,
            [.. months.Select(m => m.Month)],
            [.. unreleased.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Text)]);
        return new Notes(release, months.ToDictionary(m => m.Month, m => m.Page));
    }
}
