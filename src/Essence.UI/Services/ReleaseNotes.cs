using System.Globalization;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>
/// Where the release notes come from: release.json and the changelog-{month}.json files it lists (README → Versions).
/// <see cref="ApiError.NotFound"/> means the build has none: a development version.
/// </summary>
public interface IReleaseNotesSource
{
    Task<ApiResult<ReleaseInfo>> GetReleaseAsync(CancellationToken cancellationToken);

    Task<ApiResult<ChangelogMonth>> GetMonthAsync(string month, CancellationToken cancellationToken);
}

/// <summary>The files the web app serves under release/, next to its page.</summary>
public sealed class HttpReleaseNotesSource(HttpClient http) : IReleaseNotesSource
{
    public Task<ApiResult<ReleaseInfo>> GetReleaseAsync(CancellationToken cancellationToken) =>
        EssenceApiClient.GetAsync(http, "release.json", ReleaseJsonContext.Default.ReleaseInfo, cancellationToken);

    public Task<ApiResult<ChangelogMonth>> GetMonthAsync(string month, CancellationToken cancellationToken) =>
        EssenceApiClient.GetAsync(http, $"changelog-{month}.json", ReleaseJsonContext.Default.ChangelogMonth, cancellationToken);
}

/// <summary>A host without release notes: always a development version.</summary>
internal sealed class NoReleaseNotes : IReleaseNotesSource
{
    public Task<ApiResult<ReleaseInfo>> GetReleaseAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ApiResult<ReleaseInfo>(null, ApiError.NotFound));

    public Task<ApiResult<ChangelogMonth>> GetMonthAsync(string month, CancellationToken cancellationToken) =>
        Task.FromResult(new ApiResult<ChangelogMonth>(null, ApiError.NotFound));
}

public enum ReleaseState
{
    NotLoaded,
    Loading,
    Loaded,

    /// <summary>The build has no release notes: a development version.</summary>
    Missing,
    Failed,
}

/// <summary>
/// The release this tab runs and its changelog, a month at a time, newest first. release.json is read once, at startup:
/// after a newer release is deployed, the version shown is still the one running, and the newer versions that later
/// months' files may hold are left out. (<see cref="UpdateCheck"/> reads release.json again on its own, to notice another
/// release and offer a reload.) Failures aren't kept: the next call tries again.
/// </summary>
public sealed class ReleaseNotes(IReleaseNotesSource source)
{
    private readonly List<ChangelogVersion> _versions = [];
    private Task? _releaseLoad;
    private Task? _monthLoad;
    private int _monthsLoaded;

    public ReleaseState State { get; private set; }

    /// <summary>Once <see cref="State"/> is <see cref="ReleaseState.Loaded"/>.</summary>
    public ReleaseInfo? Release { get; private set; }

    public IReadOnlyList<ChangelogVersion> Versions => _versions;

    /// <summary>Months left to load.</summary>
    public bool HasMore => Release is { } release && _monthsLoaded < release.Months.Count;

    public bool LoadingMonth => _monthLoad is { IsCompleted: false };

    /// <summary>The last month tried failed; <see cref="LoadMoreAsync"/> tries it again.</summary>
    public bool MonthFailed { get; private set; }

    /// <summary>Reads release.json unless it's read already, or being read (the caller waits for that read).</summary>
    public Task LoadReleaseAsync()
    {
        if (State is ReleaseState.Loaded or ReleaseState.Missing)
        {
            return Task.CompletedTask;
        }

        // A finished load (failed, then) is replaced; one in flight is shared.
        if (_releaseLoad is not { IsCompleted: false })
        {
            _releaseLoad = LoadRelease();
        }

        return _releaseLoad;

        async Task LoadRelease()
        {
            State = ReleaseState.Loading;
            var result = await source.GetReleaseAsync(CancellationToken.None);
            Release = result.Value;
            State = result switch
            {
                { Value: not null } => ReleaseState.Loaded,
                { Error: ApiError.NotFound } => ReleaseState.Missing,
                _ => ReleaseState.Failed,
            };
        }
    }

    /// <summary>Loads the next month, unless none is left; while one is loading, the caller waits for it.</summary>
    public Task LoadMoreAsync()
    {
        if (!HasMore)
        {
            return Task.CompletedTask;
        }

        if (!LoadingMonth)
        {
            _monthLoad = LoadMonth();
        }

        return _monthLoad!;

        async Task LoadMonth()
        {
            var release = Release!;
            var result = await source.GetMonthAsync(release.Months[_monthsLoaded], CancellationToken.None);
            MonthFailed = result.Value is null;
            if (result.Value is { } month)
            {
                _monthsLoaded++;
                var known = _versions.Select(v => v.Date).ToHashSet();
                _versions.AddRange(month.Versions
                    .Where(v => (release.Version is null || v.Date <= release.Version) && known.Add(v.Date))
                    .OrderByDescending(v => v.Date));
            }
        }
    }

    /// <summary>The commit's page to link the commit to: only an https address, whatever the file says.</summary>
    public static string? CommitLink(ReleaseInfo release) =>
        Uri.TryCreate(release.CommitUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url.AbsoluteUri : null;

    /// <summary>"2026-10-19": how a version is written, in every language.</summary>
    public static string VersionName(DateOnly version) => version.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The entry in the UI's language: French for a French UI, English otherwise, as for the app's strings.</summary>
    public static string TextOf(ChangelogEntry entry, CultureInfo uiCulture) => IsFrench(uiCulture) ? entry.Fr : entry.En;

    /// <summary>
    /// A version's heading ("October 19, 2026", "19 octobre 2026"), with the strings' <paramref name="pattern"/>, or
    /// <paramref name="firstOfMonthPattern"/> on the 1st ("1er novembre 2026"). The UI's own culture is used when it's French
    /// or English, so its data is loaded (Blazor's ICU shard follows the browser); otherwise the English text gets English
    /// month names.
    /// </summary>
    public static string DateHeading(DateOnly date, string pattern, string firstOfMonthPattern, CultureInfo uiCulture) =>
        date.ToString(
            date.Day == 1 ? firstOfMonthPattern : pattern,
            uiCulture.TwoLetterISOLanguageName is "fr" or "en" ? uiCulture : CultureInfo.GetCultureInfo("en"));

    private static bool IsFrench(CultureInfo culture) => culture.TwoLetterISOLanguageName == "fr";
}
