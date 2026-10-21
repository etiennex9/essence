using Essence.Contracts;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class UpdateCheckTests
{
    private const string Running = "85f1af2";
    private const string Other = "aba1894";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly Source _source = new(Running);
    private readonly ReleaseNotes _notes;
    private int _changes;
    private int _reloads;

    public UpdateCheckTests() => _notes = new ReleaseNotes(_source);

    [Fact]
    public async Task A_build_without_release_notes_is_never_checked()
    {
        _source.Error = ApiError.NotFound;
        var check = await StartAsync();

        _time.Advance(UpdateCheck.Interval * 3);
        check.SetVisible(false);
        _time.Advance(UpdateCheck.AwayAfter);
        check.SetVisible(true);

        Assert.Equal(1, _source.Reads);
        Assert.False(check.Available);
    }

    [Fact]
    public async Task A_release_without_a_commit_is_never_checked()
    {
        _source.Commit = null;
        var check = await StartAsync();

        _time.Advance(UpdateCheck.Interval * 3);

        Assert.Equal(1, _source.Reads);
        Assert.False(check.Available);
    }

    [Fact]
    public async Task The_same_release_offers_nothing()
    {
        var check = await StartAsync();

        _time.Advance(UpdateCheck.Interval);

        Assert.Equal(2, _source.Reads);
        Assert.False(check.Available);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task Another_release_found_in_sight_is_offered_without_reloading()
    {
        var check = await StartAsync();

        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval);

        Assert.True(check.Available);
        Assert.Equal(1, _changes);
        Assert.Equal(0, _reloads);
    }

    [Fact]
    public async Task Another_release_found_while_away_reloads()
    {
        var check = await StartAsync();
        check.SetVisible(false);
        _time.Advance(UpdateCheck.AwayAfter);

        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval - UpdateCheck.AwayAfter);

        Assert.Equal(1, _reloads);
    }

    [Fact]
    public async Task A_page_opened_out_of_sight_reloads_once_away()
    {
        var check = Check();
        check.SetVisible(false);
        await StartAsync(check);

        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval);

        Assert.Equal(1, _reloads);
    }

    [Fact]
    public async Task A_release_found_just_after_hiding_is_read_again_once_the_page_is_away()
    {
        var check = await StartAsync();
        _time.Advance(UpdateCheck.Interval - TimeSpan.FromSeconds(30));
        check.SetVisible(false);

        _source.Commit = Other;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(check.Available);
        Assert.Equal(0, _reloads);

        _time.Advance(UpdateCheck.AwayAfter - TimeSpan.FromSeconds(30));
        Assert.Equal(3, _source.Reads);
        Assert.Equal(1, _reloads);
    }

    [Fact]
    public async Task A_failed_read_doesnt_reload()
    {
        var check = await StartAsync();
        _time.Advance(UpdateCheck.Interval - TimeSpan.FromSeconds(30));
        check.SetVisible(false);
        _source.Commit = Other;
        _time.Advance(TimeSpan.FromSeconds(30));

        _source.Error = ApiError.Failed;
        _time.Advance(UpdateCheck.AwayAfter);

        Assert.Equal(3, _source.Reads);
        Assert.True(check.Available);
        Assert.Equal(0, _reloads);
    }

    [Fact]
    public async Task Coming_back_soon_keeps_the_offer()
    {
        var check = await StartAsync();
        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval);

        check.SetVisible(false);
        _time.Advance(UpdateCheck.AwayAfter - TimeSpan.FromSeconds(1));
        check.SetVisible(true);

        Assert.Equal(2, _source.Reads);
        Assert.True(check.Available);
        Assert.Equal(0, _reloads);
    }

    [Fact]
    public async Task Coming_back_soon_only_offers_a_release_found_then()
    {
        var check = await StartAsync();
        _time.Advance(UpdateCheck.MinInterval);
        check.SetVisible(false);
        _time.Advance(TimeSpan.FromSeconds(10));

        _source.Commit = Other;
        check.SetVisible(true);

        Assert.Equal(2, _source.Reads);
        Assert.True(check.Available);
        Assert.Equal(0, _reloads);
    }

    [Fact]
    public async Task Coming_back_from_away_with_the_offer_up_checks_even_after_a_recent_check()
    {
        // Hidden; the periodic check 40 s later finds the release, and the one when the page is away, 20 s after, fails.
        var check = await StartAsync();
        _time.Advance(UpdateCheck.Interval - TimeSpan.FromSeconds(40));
        check.SetVisible(false);
        _source.Commit = Other;
        _time.Advance(TimeSpan.FromSeconds(40));
        _source.Error = ApiError.Failed;
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(3, _source.Reads);

        _source.Error = ApiError.None;
        _time.Advance(TimeSpan.FromSeconds(30));
        check.SetVisible(true);

        Assert.Equal(4, _source.Reads);
        Assert.Equal(1, _reloads);
    }

    [Fact]
    public async Task Coming_back_from_away_reloads_on_a_quick_answer()
    {
        var check = await StartAsync();
        check.SetVisible(false);
        _time.Advance(TimeSpan.FromMinutes(5));

        _source.Commit = Other;
        check.SetVisible(true);

        Assert.Equal(2, _source.Reads);
        Assert.Equal(1, _reloads);
    }

    [Fact]
    public async Task Coming_back_from_away_only_offers_on_a_slow_answer()
    {
        var check = await StartAsync();
        check.SetVisible(false);
        _time.Advance(TimeSpan.FromMinutes(5));

        _source.Commit = Other;
        var answer = _source.Hold();
        check.SetVisible(true);
        _time.Advance(UpdateCheck.ReloadWindow);
        answer.SetResult();

        Assert.True(check.Available);
        Assert.Equal(0, _reloads);
    }

    [Fact]
    public async Task Coming_back_checks_unless_the_last_check_is_recent()
    {
        var check = await StartAsync();

        _time.Advance(UpdateCheck.MinInterval - TimeSpan.FromSeconds(1));
        check.SetVisible(false);
        check.SetVisible(true);
        Assert.Equal(1, _source.Reads);

        _time.Advance(TimeSpan.FromSeconds(1));
        check.SetVisible(false);
        check.SetVisible(true);
        Assert.Equal(2, _source.Reads);
    }

    [Fact]
    public async Task A_rollback_withdraws_the_offer()
    {
        var check = await StartAsync();
        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval);
        Assert.True(check.Available);

        _source.Commit = Running;
        _time.Advance(UpdateCheck.Interval);

        Assert.False(check.Available);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public async Task A_failure_changes_nothing_and_the_next_check_asks_again()
    {
        var check = await StartAsync();

        _source.Error = ApiError.Failed;
        _time.Advance(UpdateCheck.Interval);
        Assert.False(check.Available);

        _source.Error = ApiError.None;
        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval);
        Assert.Equal(3, _source.Reads);
        Assert.True(check.Available);
    }

    [Fact]
    public async Task A_failed_first_read_is_tried_again_and_taken_as_the_running_release()
    {
        _source.Error = ApiError.Failed;
        var check = await StartAsync();
        Assert.Equal(ReleaseState.Failed, _notes.State);

        _source.Error = ApiError.None;
        _source.Commit = Other;
        _time.Advance(UpdateCheck.Interval);
        Assert.False(check.Available);

        _source.Commit = Running;
        _time.Advance(UpdateCheck.Interval);
        Assert.True(check.Available);
    }

    [Fact]
    public async Task One_read_at_a_time_decided_when_it_ends()
    {
        var check = await StartAsync();
        _source.Commit = Other;
        var answer = _source.Hold();
        _time.Advance(UpdateCheck.Interval);

        check.SetVisible(false);
        _time.Advance(UpdateCheck.AwayAfter);
        check.SetVisible(true);
        answer.SetResult();

        Assert.Equal(2, _source.Reads);
        Assert.Equal(1, _changes);
        Assert.Equal(1, _reloads);
    }

    [Fact]
    public async Task Stopping_drops_the_read_on_its_way_and_the_timers()
    {
        var check = await StartAsync();
        _source.Commit = Other;
        var answer = _source.Hold();
        _time.Advance(UpdateCheck.Interval);

        check.Stop();
        answer.SetResult();
        _time.Advance(UpdateCheck.Interval * 2);
        Assert.Equal(2, _source.Reads);
        Assert.False(check.Available);

        check.Start();
        _time.Advance(UpdateCheck.Interval);
        Assert.Equal(3, _source.Reads);
        Assert.True(check.Available);
    }

    private UpdateCheck Check()
    {
        var check = new UpdateCheck(_notes, _source, _time);
        check.Changed += () => _changes++;
        check.ReloadDue += () => _reloads++;
        return check;
    }

    private Task<UpdateCheck> StartAsync() => StartAsync(Check());

    /// <summary>As the map page does: release.json is read at first render, and checking starts once the map is up.</summary>
    private async Task<UpdateCheck> StartAsync(UpdateCheck check)
    {
        await _notes.LoadReleaseAsync();
        check.Start();
        return check;
    }

    /// <summary>release.json, with the commit and the error the test sets; an answer can be held back.</summary>
    private sealed class Source(string commit) : IReleaseNotesSource
    {
        private TaskCompletionSource? _hold;

        public string? Commit { get; set; } = commit;

        public ApiError Error { get; set; }

        public int Reads { get; private set; }

        /// <summary>The next read waits for the returned source.</summary>
        public TaskCompletionSource Hold() => _hold = new TaskCompletionSource();

        public async Task<ApiResult<ReleaseInfo>> GetReleaseAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (_hold is { } hold)
            {
                _hold = null;
                await hold.Task;
            }

            return Error == ApiError.None
                ? new ApiResult<ReleaseInfo>(new ReleaseInfo(new DateOnly(2026, 10, 20), Commit, ["2026-10"], []))
                : new ApiResult<ReleaseInfo>(null, Error);
        }

        public Task<ApiResult<ChangelogMonth>> GetMonthAsync(string month, CancellationToken cancellationToken) =>
            Task.FromResult(new ApiResult<ChangelogMonth>(new ChangelogMonth([])));
    }
}
