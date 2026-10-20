using System.Diagnostics;
using System.Text;

namespace Essence.Release;

/// <summary>A failure to report as is, without a stack trace: the run stops with this message.</summary>
public sealed class ReleaseException(string message) : Exception(message);

/// <summary>The git commands the tool needs, run in the current repository.</summary>
public sealed class Git
{
    private readonly string _directory;

    public Git(string? directory = null)
    {
        _directory = directory ?? Directory.GetCurrentDirectory();
        _directory = Run("rev-parse", "--show-toplevel").Trim();
    }

    public string Root => _directory;

    public bool IsShallow() => Run("rev-parse", "--is-shallow-repository").Trim() == "true";

    public string ShortCommit(string revision) => Run("rev-parse", "--short=7", revision).Trim();

    /// <summary>Brings origin/main and every tag up to date, overwriting local tags that differ.</summary>
    public void FetchMainAndTags() =>
        Run("fetch", "--quiet", "--force", "origin", "+refs/heads/main:refs/remotes/origin/main", "+refs/tags/*:refs/tags/*");

    /// <summary>Creates or moves the tag <paramref name="name"/> on origin to <paramref name="commit"/>.</summary>
    public void PushTag(string name, string commit) =>
        Run("push", "--quiet", "--force", "origin", $"{commit}:refs/tags/{name}");

    /// <summary>
    /// Whether <paramref name="commit"/> is on origin/main. A commit the clone doesn't have (force-pushed away) isn't:
    /// is-ancestor answers 1 for "not an ancestor" and 128 for an unknown commit.
    /// </summary>
    public bool IsOnMain(string commit) =>
        TryRun("cat-file", "-e", commit + "^{commit}").ExitCode == 0
        && TryRun("merge-base", "--is-ancestor", commit, "refs/remotes/origin/main").ExitCode == 0;

    /// <summary>The day tags (<c>vYYYY-MM-DD</c>) and the commits they point to.</summary>
    public SortedDictionary<DateOnly, string> DayTags()
    {
        var tags = new SortedDictionary<DateOnly, string>();
        foreach (var line in Lines(Run("for-each-ref", "--format=%(refname:strip=2) %(objectname) %(*objectname)", "refs/tags/v*")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (Days.TryParseTag(parts[0], out var day))
            {
                // An annotated tag's own object comes first, then the commit it points to.
                tags[day] = parts[^1];
            }
        }

        return tags;
    }

    /// <summary>The days whose tag contains <paramref name="revision"/>, oldest first.</summary>
    public List<DateOnly> DaysContaining(string revision) =>
        Lines(Run("tag", "--list", "v*", "--contains", revision))
            .Select(name => Days.TryParseTag(name, out var day) ? day : (DateOnly?)null)
            .OfType<DateOnly>()
            .Order()
            .ToList();

    /// <summary>The <c>changelog/*.json</c> files in <paramref name="revision"/>'s tree.</summary>
    /// <remarks>-z: otherwise git quotes a name with an accent ("changelog/prix-r\303\251duits.json").</remarks>
    public HashSet<string> ChangelogFiles(string revision) =>
        Run("ls-tree", "-z", "--name-only", revision, "--", EntryFiles.Folder + "/")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".json", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private string Run(params string[] args)
    {
        var (exitCode, output, error) = TryRun(args);
        return exitCode == 0 ? output : throw new ReleaseException($"git {string.Join(' ', args)} failed:\n{error.Trim()}");
    }

    private (int ExitCode, string Output, string Error) TryRun(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new ReleaseException("git couldn't be started");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
