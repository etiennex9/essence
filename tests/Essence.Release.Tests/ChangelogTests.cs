using System.Text.Json;
using Essence.Contracts;

namespace Essence.Release.Tests;

public class ChangelogTests
{
    private static readonly DateOnly Oct13 = new(2026, 10, 13);
    private static readonly DateOnly Oct14 = new(2026, 10, 14);
    private static readonly DateOnly Oct16 = new(2026, 10, 16);

    private static Entry E(string name, DateOnly? date = null) => new($"changelog/{name}.json", new ChangelogEntry($"{name} (en)", $"{name} (fr)"), date);

    private static DayTree Day(DateOnly day, params string[] names) => new(day, names.Select(n => $"changelog/{n}.json").ToHashSet());

    [Fact]
    public void An_entry_is_dated_by_the_first_day_tag_that_has_it()
    {
        var notes = Changelog.Build(Oct14, "85f1af2", [Day(Oct13, "map"), Day(Oct14, "map", "sheet")], [E("map"), E("sheet")]);

        var month = notes.Months["2026-10"];
        Assert.Equal([Oct14, Oct13], month.Versions.Select(v => v.Date));
        Assert.Equal("sheet (en)", Assert.Single(month.Versions[0].Changes).En);
        Assert.Equal("map (fr)", Assert.Single(month.Versions[1].Changes).Fr);
        Assert.Empty(notes.Release.Unreleased);
    }

    [Fact]
    public void A_written_date_wins_over_the_day_tags()
    {
        var notes = Changelog.Build(Oct14, null, [Day(Oct14, "map")], [E("map", Oct13)]);

        Assert.Equal(Oct13, Assert.Single(notes.Months["2026-10"].Versions).Date);
    }

    [Fact]
    public void An_entry_in_no_day_tag_yet_is_the_builds_own_change()
    {
        var released = Changelog.Build(Oct16, null, [Day(Oct14, "map")], [E("map"), E("refresh")]);
        Assert.Equal([Oct16, Oct14], released.Months["2026-10"].Versions.Select(v => v.Date));

        // A pull request, or a local branch: no day tag contains it, so no version, and it isn't released yet.
        var unreleased = Changelog.Build(null, null, [Day(Oct14, "map")], [E("map"), E("refresh")]);
        Assert.Equal(Oct14, Assert.Single(unreleased.Months["2026-10"].Versions).Date);
        Assert.Equal("refresh (en)", Assert.Single(unreleased.Release.Unreleased).En);
    }

    [Fact]
    public void An_older_commit_rebuilt_ignores_the_days_after_it()
    {
        // HEAD landed October 14: October 16's tag is in the clone, but not part of this version.
        var notes = Changelog.Build(Oct14, null, [Day(Oct13, "map"), Day(Oct16, "map", "refresh")], [E("map"), E("refresh")]);

        Assert.Equal([Oct14, Oct13], notes.Months["2026-10"].Versions.Select(v => v.Date));
        Assert.Equal(Oct14, notes.Release.Version);
    }

    [Fact]
    public void Versions_are_grouped_by_month_newest_first()
    {
        var notes = Changelog.Build(
            new DateOnly(2026, 12, 1),
            null,
            [],
            [E("b", Oct13), E("a", Oct13), E("c", new DateOnly(2026, 11, 30)), E("d", new DateOnly(2026, 12, 1))]);

        Assert.Equal(["2026-12", "2026-11", "2026-10"], notes.Release.Months);
        Assert.Equal(["2026-12", "2026-11", "2026-10"], notes.Months.Keys);
        // Within a day, entries go by file name.
        Assert.Equal(["a (en)", "b (en)"], notes.Months["2026-10"].Versions.Single().Changes.Select(c => c.En));
    }

    [Fact]
    public void A_day_without_entries_is_no_version()
    {
        var notes = Changelog.Build(Oct16, null, [Day(Oct13), Day(Oct14), Day(Oct16)], [E("map", Oct13)]);

        Assert.Equal(Oct13, Assert.Single(notes.Months["2026-10"].Versions).Date);
        // The version is still the day HEAD landed, even with nothing new for users that day.
        Assert.Equal(Oct16, notes.Release.Version);
    }

    [Fact]
    public void The_files_are_camel_case_with_iso_dates()
    {
        var notes = Changelog.Build(Oct14, "85f1af2", [Day(Oct14, "map")], [E("map")]);

        Assert.Equal(
            """{"version":"2026-10-14","commit":"85f1af2","months":["2026-10"],"unreleased":[]}""",
            JsonSerializer.Serialize(notes.Release, ReleaseJsonContext.Default.ReleaseInfo));
        Assert.Equal(
            """{"versions":[{"date":"2026-10-14","changes":[{"en":"map (en)","fr":"map (fr)"}]}]}""",
            JsonSerializer.Serialize(notes.Months["2026-10"], ReleaseJsonContext.Default.ChangelogMonth));
    }
}
