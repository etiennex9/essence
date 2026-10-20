using System.Globalization;
using System.Net;
using System.Resources;
using System.Text;
using Essence.Contracts;
using Essence.UI.Resources;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class ReleaseNotesTests
{
    /// <summary>release.json as tools/Essence.Release writes it.</summary>
    private const string ReleaseJson = """
        {"version":"2026-11-02","commit":"85f1af2","months":["2026-11","2026-10"],"unreleased":[],
         "commitUrl":"https://github.com/etiennex9/essence/commit/85f1af21f22bf61e2b64994f48de92b4a81bc009"}
        """;

    private const string NovemberJson = """
        {"versions":[{"date":"2026-11-02","changes":[{"en":"Older versions load as you scroll.","fr":"Les versions précédentes se chargent en défilant."}]}]}
        """;

    private const string OctoberJson = """
        {"versions":[{"date":"2026-10-16","changes":[{"en":"Prices refresh.","fr":"Les prix se mettent à jour."}]},
                     {"date":"2026-10-13","changes":[{"en":"A map.","fr":"Une carte."},{"en":"Favorites.","fr":"Des favoris."}]}]}
        """;

    [Fact]
    public async Task The_release_is_read_once_and_months_load_newest_first()
    {
        var (notes, requests) = Notes(Files(ReleaseJson, NovemberJson, OctoberJson));

        await notes.LoadReleaseAsync();
        await notes.LoadReleaseAsync();
        Assert.Equal(ReleaseState.Loaded, notes.State);
        Assert.Equal(new DateOnly(2026, 11, 2), notes.Release!.Version);
        Assert.True(notes.HasMore);

        await notes.LoadMoreAsync();
        await notes.LoadMoreAsync();
        await notes.LoadMoreAsync();

        Assert.False(notes.HasMore);
        Assert.Equal(
            [new DateOnly(2026, 11, 2), new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 13)],
            notes.Versions.Select(v => v.Date));
        Assert.Equal(["/release/release.json", "/release/changelog-2026-11.json", "/release/changelog-2026-10.json"], requests);
    }

    [Fact]
    public async Task A_month_loads_once_while_it_is_in_flight()
    {
        var month = new TaskCompletionSource<ApiResult<ChangelogMonth>>();
        var source = new SlowSource(month.Task);
        var notes = new ReleaseNotes(source);
        await notes.LoadReleaseAsync();

        var first = notes.LoadMoreAsync();
        var second = notes.LoadMoreAsync();
        Assert.True(notes.LoadingMonth);
        month.SetResult(new ApiResult<ChangelogMonth>(new ChangelogMonth([])));
        await Task.WhenAll(first, second);

        Assert.Equal(1, source.MonthRequests);
        Assert.False(notes.LoadingMonth);
    }

    [Fact]
    public async Task A_failed_month_is_tried_again()
    {
        var failures = 1;
        var (notes, requests) = Notes(request => request.RequestUri!.AbsolutePath.EndsWith("2026-11.json") && failures-- > 0
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Files(ReleaseJson, NovemberJson, OctoberJson)(request));
        await notes.LoadReleaseAsync();

        await notes.LoadMoreAsync();
        Assert.True(notes.MonthFailed);
        Assert.Empty(notes.Versions);

        await notes.LoadMoreAsync();
        Assert.False(notes.MonthFailed);
        Assert.Equal(new DateOnly(2026, 11, 2), Assert.Single(notes.Versions).Date);
        Assert.Equal(2, requests.Count(r => r.EndsWith("2026-11.json")));
    }

    [Fact]
    public async Task Versions_newer_than_the_running_one_are_left_out()
    {
        // The tab started on 2026-10-16's release; November's was deployed since, and October's file now lists the 30th.
        const string olderRelease = """{"version":"2026-10-16","commit":"8e7a1e5","months":["2026-10"],"unreleased":[]}""";
        const string october = """
            {"versions":[{"date":"2026-10-30","changes":[{"en":"Later.","fr":"Plus tard."}]},
                         {"date":"2026-10-16","changes":[{"en":"Prices refresh.","fr":"Les prix se mettent à jour."}]}]}
            """;
        var (notes, _) = Notes(Files(olderRelease, NovemberJson, october));
        await notes.LoadReleaseAsync();

        await notes.LoadMoreAsync();

        Assert.Equal(new DateOnly(2026, 10, 16), Assert.Single(notes.Versions).Date);
    }

    [Fact]
    public async Task A_build_without_release_notes_is_a_development_version()
    {
        var (notes, _) = Notes(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await notes.LoadReleaseAsync();

        Assert.Equal(ReleaseState.Missing, notes.State);
        Assert.False(notes.HasMore);
    }

    [Fact]
    public async Task A_failed_release_is_tried_again()
    {
        var failures = 1;
        var (notes, requests) = Notes(request => failures-- > 0
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Files(ReleaseJson, NovemberJson, OctoberJson)(request));

        await notes.LoadReleaseAsync();
        Assert.Equal(ReleaseState.Failed, notes.State);

        await notes.LoadReleaseAsync();
        Assert.Equal(ReleaseState.Loaded, notes.State);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("fr-CA", "Une carte.")]
    [InlineData("fr-FR", "Une carte.")]
    [InlineData("en-CA", "A map.")]
    [InlineData("de-DE", "A map.")]
    public void An_entry_is_shown_in_the_language_of_the_strings(string culture, string expected) =>
        Assert.Equal(expected, ReleaseNotes.TextOf(new ChangelogEntry("A map.", "Une carte."), CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData("fr-CA", 19, "19 octobre 2026")]
    [InlineData("fr-FR", 19, "19 octobre 2026")]
    [InlineData("fr-CA", 1, "1er octobre 2026")]
    [InlineData("en-CA", 19, "October 19, 2026")]
    [InlineData("en-US", 1, "October 1, 2026")]
    [InlineData("de-DE", 19, "October 19, 2026")]
    public void A_version_heading_is_a_date_without_the_weekday(string culture, int day, string expected)
    {
        // The patterns the UI gets for that culture from Strings.resx or Strings.fr.resx.
        var ui = CultureInfo.GetCultureInfo(culture);
        var strings = new ResourceManager(typeof(Strings).FullName!, typeof(Strings).Assembly);
        var pattern = strings.GetString("ChangelogDateFormat", ui)!;
        var firstOfMonth = strings.GetString("ChangelogDateFormatFirstDay", ui)!;

        Assert.Equal(expected, ReleaseNotes.DateHeading(new DateOnly(2026, 10, day), pattern, firstOfMonth, ui));
    }

    [Fact]
    public async Task The_commit_links_to_its_page()
    {
        var (notes, _) = Notes(Files(ReleaseJson, NovemberJson, OctoberJson));

        await notes.LoadReleaseAsync();

        Assert.Equal("https://github.com/etiennex9/essence/commit/85f1af21f22bf61e2b64994f48de92b4a81bc009", ReleaseNotes.CommitLink(notes.Release!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://github.com/etiennex9/essence/commit/85f1af2")]
    [InlineData("/commit/85f1af2")]
    public void Only_an_https_page_is_linked(string? url) =>
        Assert.Null(ReleaseNotes.CommitLink(new ReleaseInfo(new DateOnly(2026, 10, 19), "85f1af2", [], [], url)));

    [Fact]
    public void A_version_is_written_the_same_in_every_language() =>
        Assert.Equal("2026-10-19", ReleaseNotes.VersionName(new DateOnly(2026, 10, 19)));

    /// <summary>Answers release.json, then November's and October's files, as the web app serves them under release/.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Files(string release, string november, string october) =>
        request => request.RequestUri!.AbsolutePath switch
        {
            "/release/release.json" => Json(release),
            "/release/changelog-2026-11.json" => Json(november),
            "/release/changelog-2026-10.json" => Json(october),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    private static (ReleaseNotes Notes, List<string> Requests) Notes(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://web.test/release/") };
        return (new ReleaseNotes(new HttpReleaseNotesSource(http)), handler.Requests);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A release with one month, whose file arrives when the test says.</summary>
    private sealed class SlowSource(Task<ApiResult<ChangelogMonth>> monthFile) : IReleaseNotesSource
    {
        public int MonthRequests { get; private set; }

        public Task<ApiResult<ReleaseInfo>> GetReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ApiResult<ReleaseInfo>(new ReleaseInfo(new DateOnly(2026, 10, 19), "85f1af2", ["2026-10"], [])));

        public Task<ApiResult<ChangelogMonth>> GetMonthAsync(string month, CancellationToken cancellationToken)
        {
            MonthRequests++;
            return monthFile;
        }
    }
}
