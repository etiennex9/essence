using Essence.Release;

// Usage (README → Versions), from anywhere in the repository:
//   tag [--dry-run]   points each day tag (vYYYY-MM-DD) at what main was at the end of that day; the publish workflow runs
//                     it on main, with GH_TOKEN, GITHUB_REPOSITORY and GITHUB_SHA set. --dry-run only prints the moves.
//   notes <folder>    writes release.json and changelog-<month>.json, which the web app serves under release/.
try
{
    return args switch
    {
        ["tag"] => await TagCommand.RunAsync(dryRun: false),
        ["tag", "--dry-run"] => await TagCommand.RunAsync(dryRun: true),
        ["notes", var folder] => NotesCommand.Run(folder),
        _ => Usage(),
    };
}
catch (ReleaseException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: Essence.Release tag [--dry-run] | notes <folder>");
    return 2;
}
