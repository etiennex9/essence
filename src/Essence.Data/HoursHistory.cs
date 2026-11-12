using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Essence.Data;

/// <summary>What an hours source's runs say, read the same way by the ingest (its schedule and gate) and the API (its health).</summary>
/// <param name="LastSuccess">The latest run that fetched good data: <see cref="HoursImportStatus.Succeeded"/> or <see cref="HoursImportStatus.Unchanged"/>.</param>
/// <param name="LastGood">The latest run whose matches are the rows serving now: a success, or a <see cref="HoursImportStatus.Rematched"/> run since.</param>
/// <param name="LastAttempt">The latest fetch, whatever came of it. Re-matches don't fetch, so they're left out.</param>
/// <param name="FailuresSinceSuccess">Failed or rejected fetches since <paramref name="LastSuccess"/>, or since the first run when there's none.</param>
/// <param name="Disabled">
/// The latest run is <see cref="HoursImportStatus.Disabled"/>: the source is off, and has no rows. All the above only
/// counts runs after the latest such one, so a source turned on again starts over.
/// </param>
public sealed record HoursSourceHistory(
    string Source,
    HoursImport? LastSuccess,
    HoursImport? LastGood,
    HoursImport? LastAttempt,
    int FailuresSinceSuccess,
    bool Disabled)
{
    /// <summary>
    /// Whether the rows serving now are the matches of the download <paramref name="version"/>: only then may that
    /// download be matched again. One kept on disk can be older, when keeping a newer one failed after its rows were
    /// written, and matching it again would put the older hours back.
    /// </summary>
    public bool Serves(string version) => LastGood?.SourceVersion == version;
}

public static class HoursHistory
{
    /// <summary>A source that never succeeded is read from its latest runs only: enough to count its failures in a row.</summary>
    private const int MaxRunsWithoutSuccess = 1000;

    /// <summary>A success: the source answered with data that was matched and written, changed or not.</summary>
    public static bool IsSuccess(HoursImportStatus status) => status is HoursImportStatus.Succeeded or HoursImportStatus.Unchanged;

    /// <summary>Every source that has runs and isn't turned off (<see cref="HoursImportStatus.Disabled"/>), by name.</summary>
    public static async Task<IReadOnlyList<HoursSourceHistory>> LoadAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        var sources = await db.HoursImports.Select(i => i.Source).Distinct().OrderBy(s => s).ToListAsync(cancellationToken);
        var histories = new List<HoursSourceHistory>(sources.Count);
        foreach (var source in sources)
        {
            if (await LoadAsync(db, source, cancellationToken) is { Disabled: false } history)
            {
                histories.Add(history);
            }
        }

        return histories;
    }

    /// <summary>One source's runs, from its last success on.</summary>
    public static async Task<HoursSourceHistory> LoadAsync(EssenceDbContext db, string source, CancellationToken cancellationToken)
    {
        var runs = db.HoursImports.AsNoTracking().Where(i => i.Source == source);
        var lastSuccessAt = await runs
            .Where(i => i.Status == HoursImportStatus.Succeeded || i.Status == HoursImportStatus.Unchanged)
            .MaxAsync(i => (DateTimeOffset?)i.FinishedAt, cancellationToken);
        var since = lastSuccessAt is { } at ? runs.Where(i => i.FinishedAt >= at) : runs;
        var rows = await since
            .OrderByDescending(i => i.FinishedAt)
            .ThenByDescending(i => i.Id)
            .Take(MaxRunsWithoutSuccess)
            .ToListAsync(cancellationToken);
        return Summarize(source, rows);
    }

    /// <summary>
    /// Reads a source's runs, in any order: from its last success on is enough, and earlier ones change nothing. Nor do
    /// those before the latest <see cref="HoursImportStatus.Disabled"/> run: the rows they wrote are gone.
    /// </summary>
    public static HoursSourceHistory Summarize(string source, IEnumerable<HoursImport> runs)
    {
        var newestFirst = runs.OrderByDescending(i => i.FinishedAt).ThenByDescending(i => i.Id).ToList();
        var disabled = newestFirst.FirstOrDefault()?.Status == HoursImportStatus.Disabled;
        var current = newestFirst.TakeWhile(i => i.Status != HoursImportStatus.Disabled).ToList();
        var lastSuccess = current.FirstOrDefault(i => IsSuccess(i.Status));
        var lastGood = current.FirstOrDefault(i => IsSuccess(i.Status) || i.Status == HoursImportStatus.Rematched);
        var fetches = current.Where(i => i.Status != HoursImportStatus.Rematched).ToList();
        var failures = fetches.TakeWhile(i => !IsSuccess(i.Status)).Count();
        return new HoursSourceHistory(source, lastSuccess, lastGood, fetches.FirstOrDefault(), failures, disabled);
    }
}
