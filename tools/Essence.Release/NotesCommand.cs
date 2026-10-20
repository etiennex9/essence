using System.Text.Json;
using Essence.Contracts;

namespace Essence.Release;

/// <summary>Writes the release notes of HEAD: its version (the first day tag containing it) and the changelog up to it.</summary>
public static class NotesCommand
{
    public static int Run(string folder)
    {
        var git = new Git();
        // What goes to production: a push to main, or a manual run on it. Pull requests and local builds have no version.
        var release = Environment.GetEnvironmentVariable("GITHUB_REF") == "refs/heads/main"
                      && Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") is "push" or "workflow_dispatch";
        if (release && git.IsShallow())
        {
            throw new ReleaseException("The clone is shallow, so it has no day tags: check it out with fetch-depth: 0.");
        }

        DateOnly? version = git.DaysContaining("HEAD") is [var first, ..] ? first : null;
        if (release && version is null)
        {
            throw new ReleaseException("No day tag contains HEAD. The tag job sets them: check that it ran, then re-run this job.");
        }

        var days = git.DayTags().Select(t => new DayTree(t.Key, git.ChangelogFiles(t.Value))).ToList();
        var notes = Changelog.Build(version, git.ShortCommit("HEAD"), days, EntryFiles.ReadAll(git.Root));
        notes = notes with { Release = notes.Release with { CommitUrl = CommitUrl(git) } };
        Write(folder, notes);

        Console.WriteLine($"{(version is { } v ? $"Version {Days.Iso(v)}" : "No version")} ({notes.Release.Commit}), in {Path.GetFullPath(folder)}");
        foreach (var (month, page) in notes.Months)
        {
            Console.WriteLine($"  {month}: {string.Join(", ", page.Versions.Select(p => $"{Days.Iso(p.Date)} ({p.Changes.Count})"))}");
        }

        Console.WriteLine($"  not released: {notes.Release.Unreleased.Count}");
        return 0;
    }

    /// <summary>HEAD's page on GitHub, from the workflow's variables (https://github.com/owner/name/commit/sha); none locally.</summary>
    private static string? CommitUrl(Git git) =>
        Environment.GetEnvironmentVariable("GITHUB_SERVER_URL") is { Length: > 0 } server
        && Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { Length: > 0 } repository
            ? $"{server}/{repository}/commit/{git.Commit("HEAD")}"
            : null;

    private static void Write(string folder, Notes notes)
    {
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "changelog-*.json"))
        {
            File.Delete(old);
        }

        File.WriteAllText(Path.Combine(folder, "release.json"), JsonSerializer.Serialize(notes.Release, ReleaseJsonContext.Default.ReleaseInfo));
        foreach (var (month, page) in notes.Months)
        {
            File.WriteAllText(Path.Combine(folder, $"changelog-{month}.json"), JsonSerializer.Serialize(page, ReleaseJsonContext.Default.ChangelogMonth));
        }
    }
}
