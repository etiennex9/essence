namespace Essence.Release;

/// <summary>An entry of GitHub's activity log for main: main moved from <see cref="Before"/> to <see cref="After"/>.</summary>
/// <param name="ActivityType">push, force_push, pr_merge, merge_queue_merge, branch_creation or branch_deletion.</param>
public sealed record Activity(string Before, string After, DateTimeOffset Timestamp, string ActivityType);

/// <summary>Where each day's tag goes: <c>vD</c> is what main was at the end of day D.</summary>
public static class DayTags
{
    /// <summary>
    /// For each day in <paramref name="activities"/>, the newest commit main moved to that day which is still on main
    /// (<paramref name="isOnMain"/>), so a push that a later force push replaced doesn't count. A day with none of them
    /// left gets no tag.
    /// </summary>
    public static SortedDictionary<DateOnly, string> Plan(IEnumerable<Activity> activities, Func<string, bool> isOnMain)
    {
        var plan = new SortedDictionary<DateOnly, string>();
        foreach (var activity in activities.Where(a => a.ActivityType != "branch_deletion").OrderByDescending(a => a.Timestamp))
        {
            var day = Days.Of(activity.Timestamp);
            if (!plan.ContainsKey(day) && isOnMain(activity.After))
            {
                plan[day] = activity.After;
            }
        }

        return plan;
    }

    /// <summary>The day <paramref name="commit"/> first landed on main, or null when the log doesn't show it landing.</summary>
    public static DateOnly? LandingDay(IEnumerable<Activity> activities, string commit) =>
        activities
            .Where(a => a.ActivityType != "branch_deletion" && a.After == commit)
            .OrderBy(a => a.Timestamp)
            .Select(a => (DateOnly?)Days.Of(a.Timestamp))
            .FirstOrDefault();
}
