using Essence.Data;
using Essence.Data.Entities;

namespace Essence.Ingest.Tests;

/// <summary>How a source's runs are read, by the ingest's schedule and gate as by the API's health.</summary>
public sealed class HoursHistoryTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 12, 7, 0, 0, TimeSpan.Zero);

    private static HoursImport Run(long id, int day, HoursImportStatus status, int matched = 18) => new()
    {
        Id = id,
        Source = "costco",
        StartedAt = Day.AddDays(day),
        FinishedAt = Day.AddDays(day).AddMinutes(1),
        Status = status,
        Matched = matched,
        SourceVersion = $"v{id}",
    };

    [Fact]
    public void Failures_since_the_last_success_count_and_re_matches_are_neither_successes_nor_attempts()
    {
        var history = HoursHistory.Summarize("costco",
        [
            Run(1, 0, HoursImportStatus.Succeeded),
            Run(2, 1, HoursImportStatus.Failed),
            Run(3, 2, HoursImportStatus.Rematched, matched: 17),
            Run(4, 3, HoursImportStatus.Rejected, matched: 2),
        ]);

        Assert.Equal(1, history.LastSuccess!.Id);
        Assert.Equal(3, history.LastGood!.Id);
        Assert.Equal(17, history.LastGood.Matched);
        Assert.Equal(4, history.LastAttempt!.Id);
        Assert.Equal(2, history.FailuresSinceSuccess);
    }

    [Fact]
    public void An_unchanged_run_is_a_success()
    {
        var history = HoursHistory.Summarize("costco",
        [
            Run(2, 1, HoursImportStatus.Failed),
            Run(1, 0, HoursImportStatus.Succeeded),
            Run(3, 7, HoursImportStatus.Unchanged),
        ]);

        Assert.Equal(3, history.LastSuccess!.Id);
        Assert.Equal(3, history.LastGood!.Id);
        Assert.Equal(0, history.FailuresSinceSuccess);
    }

    [Fact]
    public void A_source_that_never_succeeded_counts_every_failure()
    {
        var history = HoursHistory.Summarize("costco",
            [Run(1, 0, HoursImportStatus.Failed), Run(2, 0, HoursImportStatus.Failed), Run(3, 1, HoursImportStatus.Rejected)]);

        Assert.Null(history.LastSuccess);
        Assert.Null(history.LastGood);
        Assert.Equal(3, history.LastAttempt!.Id);
        Assert.Equal(3, history.FailuresSinceSuccess);
    }

    [Fact]
    public void A_source_turned_off_is_disabled_and_has_nothing_serving()
    {
        var history = HoursHistory.Summarize("costco",
            [Run(1, 0, HoursImportStatus.Succeeded), Run(2, 1, HoursImportStatus.Failed), Run(3, 2, HoursImportStatus.Disabled)]);

        Assert.True(history.Disabled);
        Assert.Null(history.LastSuccess);
        Assert.Null(history.LastGood);
        Assert.Null(history.LastAttempt);
        Assert.Equal(0, history.FailuresSinceSuccess);
        Assert.False(history.Serves("v1"));
    }

    [Fact]
    public void A_source_turned_on_again_starts_over_from_its_runs_since()
    {
        var history = HoursHistory.Summarize("costco",
        [
            Run(1, 0, HoursImportStatus.Succeeded),
            Run(2, 1, HoursImportStatus.Disabled),
            Run(3, 9, HoursImportStatus.Failed),
            Run(4, 9, HoursImportStatus.Rejected),
        ]);

        Assert.False(history.Disabled);
        Assert.Null(history.LastSuccess);
        Assert.Null(history.LastGood);
        Assert.Equal(4, history.LastAttempt!.Id);
        Assert.Equal(2, history.FailuresSinceSuccess);
    }

    [Fact]
    public void Only_the_download_whose_rows_are_serving_may_be_matched_again()
    {
        var rematch = Run(3, 8, HoursImportStatus.Rematched);
        rematch.SourceVersion = "v2";
        var history = HoursHistory.Summarize("costco",
            [Run(1, 0, HoursImportStatus.Succeeded), Run(2, 7, HoursImportStatus.Succeeded), rematch, Run(4, 14, HoursImportStatus.Rejected)]);

        Assert.True(history.Serves("v2"));
        // The week before's, still on disk when keeping v2's download failed: it would put older hours back.
        Assert.False(history.Serves("v1"));
        // What the gate rejected never served.
        Assert.False(history.Serves("v4"));
        Assert.False(HoursHistory.Summarize("costco", [Run(1, 0, HoursImportStatus.Failed)]).Serves("v1"));
    }
}
