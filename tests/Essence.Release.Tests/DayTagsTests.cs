namespace Essence.Release.Tests;

public class DayTagsTests
{
    /// <summary>main's real activity log, 2026-10-12 to 10-19: every move a direct push, several late in the evening.</summary>
    private static readonly List<Activity> Log =
        GitHub.ParseActivity(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "activity.json")));

    [Fact]
    public void The_log_is_read_with_its_types_and_times()
    {
        Assert.Equal(12, Log.Count);
        var creation = Log.Single(a => a.ActivityType == "branch_creation");
        Assert.Equal("d3f4e96", creation.After[..7]);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 2, 10, 47, TimeSpan.Zero), creation.Timestamp);
    }

    [Fact]
    public void Each_day_is_where_main_was_at_its_end_in_quebec()
    {
        var plan = DayTags.Plan(Log, _ => true);

        // In UTC, five of the twelve moves would fall on the next day: the evening ones (UTC-4 in October).
        Assert.Equal(
            new Dictionary<DateOnly, string>
            {
                [new(2026, 10, 12)] = "d3f4e96", // 22:10, the branch's creation
                [new(2026, 10, 13)] = "06f1a06", // 23:45, the fifth push that day
                [new(2026, 10, 14)] = "f21f573", // 00:44
                [new(2026, 10, 16)] = "8e7a1e5", // 01:23
                [new(2026, 10, 17)] = "9a2735a",
                [new(2026, 10, 19)] = "85f1af2", // 21:04, 01:04 UTC on the 20th
            },
            plan.ToDictionary(p => p.Key, p => p.Value[..7]));
    }

    [Fact]
    public void A_move_no_longer_on_main_gives_way_to_the_days_last_one_that_is()
    {
        // 06f1a06 force-pushed away: 23:20's push ends October 13. 85f1af2 was October 19's only one: no tag.
        var gone = Log.Where(a => a.After[..7] is "06f1a06" or "85f1af2").Select(a => a.After).ToHashSet();

        var plan = DayTags.Plan(Log, commit => !gone.Contains(commit));

        Assert.Equal("14623bc", plan[new DateOnly(2026, 10, 13)][..7]);
        Assert.False(plan.ContainsKey(new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public void Branch_deletions_are_not_moves()
    {
        var log = new List<Activity>
        {
            new("a", "b", new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero), "push"),
            new("b", "0000000000000000000000000000000000000000", new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero), "branch_deletion"),
        };

        Assert.Equal("b", DayTags.Plan(log, _ => true)[new DateOnly(2026, 10, 20)]);
    }

    [Fact]
    public void A_commit_lands_on_the_day_of_its_first_move()
    {
        var log = new List<Activity>
        {
            new("a", "b", new DateTimeOffset(2026, 10, 21, 3, 59, 0, TimeSpan.Zero), "pr_merge"), // 23:59 on the 20th
            new("b", "c", new DateTimeOffset(2026, 10, 21, 4, 10, 0, TimeSpan.Zero), "push"),
            new("c", "b", new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero), "force_push"),
        };

        Assert.Equal(new DateOnly(2026, 10, 20), DayTags.LandingDay(log, "b"));
        Assert.Equal(new DateOnly(2026, 10, 21), DayTags.LandingDay(log, "c"));
        Assert.Null(DayTags.LandingDay(log, "d"));
    }

    [Fact]
    public void Days_follow_daylight_saving_time()
    {
        // November 1, 2026 starts at 04:00 UTC (EDT) and ends at 05:00 UTC on the 2nd (EST).
        Assert.Equal(new DateOnly(2026, 10, 31), Days.Of(new DateTimeOffset(2026, 11, 1, 3, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 1), Days.Of(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 1), Days.Of(new DateTimeOffset(2026, 11, 2, 4, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 2), Days.Of(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("v2026-10-19", true)]
    [InlineData("v2026-02-30", false)]
    [InlineData("2026-10-19", false)]
    [InlineData("v2026-10-19-2", false)]
    [InlineData("v1.0.0", false)]
    public void Only_exact_day_tags_are_day_tags(string name, bool isDayTag)
    {
        Assert.Equal(isDayTag, Days.TryParseTag(name, out var day));
        if (isDayTag)
        {
            Assert.Equal(name, Days.TagName(day));
        }
    }
}
