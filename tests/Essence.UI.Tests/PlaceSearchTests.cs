using System.Net;
using System.Text;
using Essence.Contracts;
using Essence.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace Essence.UI.Tests;

public class PlaceSearchTests
{
    private const string GauchetiereJson = """
        [{"label":"1000 Rue De La Gauchetière Ouest","detail":"Montréal, QC H3A 4W5","latitude":45.49876,"longitude":-73.56657,"kind":"address"},
         {"label":"Rue De La Gauchetière Est","detail":"Montréal","latitude":45.5153,"longitude":-73.5537,"kind":"street"},
         {"label":"Rue Gaucher","detail":"Marieville","latitude":45.4277,"longitude":-73.1483,"kind":"street"}]
        """;

    private static readonly GeoPoint Montreal = new(45.501711, -73.567321);

    private readonly FakeTimeProvider _time = new();
    private readonly List<Uri> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Json(HttpStatusCode.OK, GauchetiereJson);

    private PlaceSearch NewSearch() =>
        new(new EssenceApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = new Uri("http://api.test/") }), _time);

    [Fact]
    public async Task What_is_typed_is_searched_300_ms_after_the_last_keystroke()
    {
        var search = NewSearch();

        search.Type("100", Montreal, "fr");
        search.Type("1000", Montreal, "fr");
        search.Type("1000 de la gauch", Montreal, "fr");
        _time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Empty(_requests);
        Assert.True(search.Loading);

        await AnsweredAsync(search, () => _time.Advance(TimeSpan.FromMilliseconds(1)));

        Assert.Equal("/places/search?q=1000%20de%20la%20gauch&lat=45.5017&lon=-73.5673&lang=fr", Assert.Single(_requests).PathAndQuery);
        Assert.Equal(3, search.Suggestions.Count);
        Assert.True(search.Answered);
        Assert.False(search.Loading);
    }

    [Fact]
    public async Task Under_three_characters_nothing_is_searched()
    {
        var search = NewSearch();
        search.Type("10", Montreal, "fr");
        search.Type("  10  ", Montreal, "fr");

        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();

        Assert.Empty(_requests);
        Assert.False(search.Searching);
        Assert.Empty(search.Suggestions);
    }

    [Fact]
    public async Task Going_back_to_a_text_already_searched_shows_its_answers_at_once()
    {
        var search = NewSearch();
        search.Type("gauch", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));
        search.Type("gauche", Montreal, "fr");

        search.Type("Gauch ", Montreal, "fr");

        Assert.True(search.Answered);
        Assert.False(search.Loading);
        Assert.Equal(3, search.Suggestions.Count);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(_requests);
    }

    [Fact]
    public async Task While_a_longer_text_is_on_its_way_the_shorter_one_s_answers_that_still_match_show()
    {
        var search = NewSearch();
        search.Type("gauch", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));

        search.Type("gauche", Montreal, "fr");

        Assert.False(search.Answered);
        Assert.True(search.Loading);
        Assert.Equal(3, search.Suggestions.Count);

        search.Type("gauchet", Montreal, "fr");

        Assert.Equal(["1000 Rue De La Gauchetière Ouest", "Rue De La Gauchetière Est"], search.Suggestions.Select(s => s.Label));
    }

    [Fact]
    public async Task Answers_are_kept_per_language_and_area()
    {
        var search = NewSearch();
        search.Type("gaucher", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));

        search.Type("gaucher", Montreal, "en");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));
        search.Type("gaucher", new GeoPoint(46.81, -71.21), "en");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));
        search.Type("gaucher", null, "en");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));

        Assert.Equal(4, _requests.Count);
        Assert.Equal("/places/search?q=gaucher&lang=en", _requests[3].PathAndQuery);
    }

    [Fact]
    public async Task A_newer_keystroke_cancels_the_search_on_its_way()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var search = new PlaceSearch(
            new EssenceApiClient(new HttpClient(new AsyncHandler(this, () =>
            {
                if (first)
                {
                    first = false;
                    return gate.Task;
                }

                return Task.FromResult(Json(HttpStatusCode.OK, """[{"label":"Rue Gaucher","detail":"Marieville","latitude":45.4,"longitude":-73.1,"kind":"street"}]"""));
            })) { BaseAddress = new Uri("http://api.test/") }),
            _time);
        search.Type("gauch", Montreal, "fr");
        _time.Advance(PlaceSearch.Debounce);

        search.Type("gaucher", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));
        gate.SetResult(Json(HttpStatusCode.OK, GauchetiereJson));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("gaucher", search.Text);
        Assert.Equal(["Rue Gaucher"], search.Suggestions.Select(s => s.Label));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiError.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ApiError.Failed)]
    public async Task A_failure_shows_no_suggestions_and_says_why(HttpStatusCode status, ApiError error)
    {
        var search = NewSearch();
        search.Type("gauch", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));
        _respond = _ => Json(status, "{}");

        search.Type("gauchet", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));

        Assert.Equal(error, search.Error);
        Assert.Empty(search.Suggestions);
        Assert.False(search.Answered);

        // Not kept: the same text asks again.
        _respond = _ => Json(HttpStatusCode.OK, GauchetiereJson);
        search.Type("gauche", Montreal, "fr");
        search.Type("gauchet", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));
        Assert.Equal(ApiError.None, search.Error);
        Assert.Equal(3, search.Suggestions.Count);
    }

    [Fact]
    public void Clearing_empties_the_text_and_the_suggestions()
    {
        var search = NewSearch();
        search.Type("gauch", Montreal, "fr");

        search.Clear();

        Assert.Equal("", search.Text);
        Assert.Empty(search.Suggestions);
        Assert.False(search.Loading);
    }

    [Theory]
    [InlineData("1000 de la gauch", true)]
    [InlineData("gauche ouest", true)]
    [InlineData("GAUCHETIÈRE montreal", true)]
    [InlineData("1000 gaucher", false)]
    [InlineData("ouest est", false)]
    public void A_suggestion_matches_when_every_word_typed_starts_one_of_its_words(string typed, bool matches) =>
        Assert.Equal(matches, PlaceSearch.Matches(
            new PlaceSuggestion("1000 Rue De La Gauchetière Ouest", "Montréal, QC H3A 4W5", 45.5, -73.6, "address"), typed));

    [Fact]
    public void The_words_typed_are_found_at_the_starts_of_the_label_s_words()
    {
        var spans = PlaceSearch.Highlights("1000 Rue De La Gauchetière Ouest", "1000 de la GAUCH");

        Assert.Equal([(0, 4), (9, 2), (12, 2), (15, 5)], spans);
    }

    [Fact]
    public async Task An_emoji_typed_or_in_a_label_is_searched_and_highlighted_like_any_text()
    {
        var search = NewSearch();

        search.Type("📍 1000 Rue De La Gauch", Montreal, "fr");
        await AnsweredAsync(search, () => _time.Advance(PlaceSearch.Debounce));

        Assert.Equal(3, search.Suggestions.Count);
        Assert.Equal([(0, 4), (9, 2), (12, 2), (15, 5)], PlaceSearch.Highlights("1000 Rue De La Gauchetière Ouest", "📍 1000 de la gauch"));
        Assert.Equal([(8, 5)], PlaceSearch.Highlights("Café 📍 Gaucher", "gauch"));
        Assert.True(PlaceSearch.Matches(new PlaceSuggestion("Café 📍 Gaucher", "Montréal", 45.5, -73.6, "named"), "café gau"));
    }

    [Fact]
    public void Accents_are_matched_without_moving_the_highlights() =>
        Assert.Equal([(4, 5)], PlaceSearch.Highlights("Rue Évangéline", "evang"));

    private static async Task AnsweredAsync(PlaceSearch search, Action act)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (!search.Loading)
            {
                changed.TrySetResult();
            }
        }

        search.Changed += OnChanged;
        try
        {
            act();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            search.Changed -= OnChanged;
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(PlaceSearchTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            test._requests.Add(request.RequestUri!);
            return Task.FromResult(test._respond(request));
        }
    }

    private sealed class AsyncHandler(PlaceSearchTests test, Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            return await respond().WaitAsync(cancellationToken);
        }
    }
}
