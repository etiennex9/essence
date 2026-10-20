namespace Essence.Release;

/// <summary>
/// Points the day tags at where main was at the end of each day, from GitHub's activity log: the time GitHub saw main move,
/// not a commit's own date nor when this run started. Every run re-derives each day in the log's newest 100 moves, so a run
/// that was replaced while it waited, or that failed, is made up for by the next one.
/// </summary>
public static class TagCommand
{
    /// <summary>The push that started the run normally shows up within a second or two.</summary>
    private static readonly TimeSpan WaitForPush = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(bool dryRun)
    {
        var git = new Git();
        var github = GitHub.FromEnvironment();
        // The commit this run builds. Without it (trying --dry-run by hand), no day is required.
        var commit = Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } sha ? sha : null;

        var activity = await ReadActivityAsync(github, commit);
        // After reading the log, so origin/main is at least as new as what the log shows.
        git.FetchMainAndTags();
        var plan = DayTags.Plan(activity, git.IsOnMain);

        DateOnly? required = null;
        if (commit is not null)
        {
            if (!git.IsOnMain(commit))
            {
                throw new ReleaseException($"{commit[..7]} isn't on main anymore (force-pushed away?): the run for main's new tip releases it instead.");
            }

            required = DayTags.LandingDay(activity, commit);
        }

        var tags = git.DayTags();
        foreach (var (day, target) in plan)
        {
            var name = Days.TagName(day);
            var current = tags.GetValueOrDefault(day);
            if (current == target)
            {
                continue;
            }

            var change = current is null ? $"{name} → {target[..7]}" : $"{name} → {target[..7]} (was {current[..7]})";
            if (dryRun)
            {
                Console.WriteLine($"would set {change}");
                continue;
            }

            try
            {
                await github.SetTagAsync(name, target, exists: current is not null, CancellationToken.None);
                Console.WriteLine($"set {change}");
            }
            catch (ReleaseException ex) when (day != required)
            {
                // Only the day of the commit being built has to be right now. Another day's tag (an earlier run's, or a
                // first run's backfill) is retried by the next run; until then its entries are dated by the next day tag.
                Console.WriteLine($"::warning::couldn't set {change}: {ex.Message}");
            }
        }

        return 0;
    }

    private static async Task<List<Activity>> ReadActivityAsync(GitHub github, string? commit)
    {
        var deadline = DateTimeOffset.UtcNow + WaitForPush;
        while (true)
        {
            var activity = await github.GetMainActivityAsync(CancellationToken.None);
            if (commit is null || activity.Any(a => a.After == commit))
            {
                return activity;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new ReleaseException(
                    $"GitHub's activity log doesn't show main moving to {commit[..7]} after {WaitForPush.TotalSeconds:0} s. Re-run this job in a few minutes.");
            }

            await Task.Delay(PollInterval);
        }
    }
}
