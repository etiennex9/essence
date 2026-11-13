using System.Net;
using System.Text;
using System.Web;
using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

/// <summary>
/// Linking the device's own OpenStreetMap account: OAuth 2 with PKCE as a public client, the state checked, the token kept on
/// the device only, the application named by the Essence server.
/// </summary>
public class OsmAccountTests
{
    private const string ConfigJson = """{"osmClientId":"client-abc","osmAuthUrl":"https://www.openstreetmap.org","osmApiUrl":"https://api.openstreetmap.org"}""";
    private const string Redirect = "https://example.com/osm-auth.html";

    private static readonly OsmHoursEdit Hours =
        new(new OsmElementRef("node", 1), "24/7", new DateOnly(2026, 11, 13), "Essence", "Update", false);

    private readonly MemoryTokens _tokens = new();
    private string _config = ConfigJson;
    private Func<Uri, OsmCallback?> _answer = url => new OsmCallback("code-1", Query(url)["state"]);
    private readonly OsmStub _osm;

    public OsmAccountTests()
    {
        _osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsoluteUri) switch
        {
            ("GET", "http://api.test/client-config") => _config.Length == 0
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(_config),
            ("POST", "https://www.openstreetmap.org/oauth2/token") => Json("""{"access_token":"token-xyz","token_type":"Bearer","scope":"write_api read_prefs"}"""),
            ("GET", "https://api.openstreetmap.org/api/0.6/user/details.json") => Json("""{"version":"0.6","user":{"id":7,"display_name":"Mapper & Co"}}"""),
            ("POST", "https://www.openstreetmap.org/oauth2/revoke") => Json("{}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    [Fact]
    public void The_challenge_is_the_verifier_s_SHA_256_in_base64url_as_RFC_7636_s_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OsmOAuth.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void A_verifier_is_43_url_safe_characters_and_each_one_is_new()
    {
        var verifier = OsmOAuth.NewVerifier();

        Assert.Equal(43, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.NotEqual(verifier, OsmOAuth.NewVerifier());
        Assert.NotEqual(OsmOAuth.NewState(), OsmOAuth.NewState());
    }

    [Fact]
    public void The_state_must_be_the_one_sent()
    {
        Assert.True(OsmOAuth.StateMatches("abc", "abc"));
        Assert.False(OsmOAuth.StateMatches("abc", "abd"));
        Assert.False(OsmOAuth.StateMatches("abc", null));
        Assert.False(OsmOAuth.StateMatches("abc", ""));
    }

    [Fact]
    public async Task Linking_signs_in_with_PKCE_trades_the_code_and_keeps_the_token_on_the_device()
    {
        Uri? authorize = null;
        _answer = url =>
        {
            authorize = url;
            return new OsmCallback("code-1", Query(url)["state"]);
        };
        var account = Account();

        Assert.Equal(OsmLinkOutcome.Linked, await account.LinkAsync());

        var query = Query(authorize!);
        Assert.Equal("https://www.openstreetmap.org/oauth2/authorize", authorize!.GetLeftPart(UriPartial.Path));
        Assert.Equal(("code", "client-abc", Redirect, "write_api read_prefs", "S256"),
            (query["response_type"], query["client_id"], query["redirect_uri"], query["scope"], query["code_challenge_method"]));
        var token = HttpUtility.ParseQueryString(_osm.Requests.Single(r => r.Path == "/oauth2/token").Body);
        Assert.Equal(("authorization_code", "code-1", Redirect, "client-abc"), (token["grant_type"], token["code"], token["redirect_uri"], token["client_id"]));
        Assert.Equal(query["code_challenge"], OsmOAuth.ChallengeOf(token["code_verifier"]!));
        Assert.Equal("Bearer token-xyz", _osm.Requests.Single(r => r.Path == "/api/0.6/user/details.json").Authorization);

        Assert.True(account.IsLinked);
        Assert.Equal("Mapper & Co", account.DisplayName);
        Assert.Contains("token-xyz", _tokens.Value);
        Assert.DoesNotContain(_osm.Requests, r => r.Url.Host == "api.test" && r.Path != "/client-config");

        var later = Account();
        await later.LoadAsync();
        Assert.True(later.IsLinked);
        Assert.Equal("Mapper & Co", later.DisplayName);
    }

    [Fact]
    public async Task An_answer_to_another_sign_in_is_never_traded_for_a_token()
    {
        _answer = _ => new OsmCallback("code-1", "someone-else-s-state");
        var account = Account();

        Assert.Equal(OsmLinkOutcome.Failed, await account.LinkAsync());

        Assert.False(account.IsLinked);
        Assert.DoesNotContain(_osm.Requests, r => r.Path == "/oauth2/token");
        Assert.Null(_tokens.Value);
    }

    [Theory]
    [InlineData("access_denied", OsmLinkOutcome.Cancelled)]
    [InlineData("popup_blocked", OsmLinkOutcome.Blocked)]
    [InlineData("invalid_scope", OsmLinkOutcome.Failed)]
    public async Task OpenStreetMap_s_errors_are_told_apart(string error, OsmLinkOutcome outcome)
    {
        _answer = url => new OsmCallback(null, Query(url)["state"], error);

        Assert.Equal(outcome, await Account().LinkAsync());
        Assert.DoesNotContain(_osm.Requests, r => r.Path == "/oauth2/token");
    }

    [Fact]
    public async Task Giving_up_the_sign_in_links_nothing()
    {
        _answer = _ => null;

        Assert.Equal(OsmLinkOutcome.Cancelled, await Account().LinkAsync());
        Assert.Null(_tokens.Value);
    }

    [Fact]
    public async Task Without_an_application_on_the_server_editing_OpenStreetMap_is_off()
    {
        _config = """{"osmClientId":null,"osmAuthUrl":"https://www.openstreetmap.org","osmApiUrl":"https://api.openstreetmap.org"}""";
        var account = Account();

        await account.LoadAsync();

        Assert.False(account.IsAvailable);
        Assert.Equal(OsmLinkOutcome.Failed, await account.LinkAsync());
        Assert.DoesNotContain(_osm.Requests, r => r.Url.Host != "api.test");
    }

    [Fact]
    public async Task An_older_server_without_settings_is_off_too()
    {
        _config = "";
        var account = Account();

        await account.LoadAsync();

        Assert.False(account.IsAvailable);
    }

    [Fact]
    public async Task An_account_linked_on_another_OpenStreetMap_isn_t_this_one_s()
    {
        _tokens.Value = """
            {"accessToken":"dev-token","authUrl":"https://master.apis.dev.openstreetmap.org/","apiUrl":"https://master.apis.dev.openstreetmap.org/","clientId":"dev","displayName":"Tester"}
            """;
        var account = Account();

        await account.LoadAsync();

        Assert.True(account.IsAvailable);
        Assert.False(account.IsLinked);
        Assert.Null(account.DisplayName);
    }

    [Fact]
    public async Task An_account_kept_without_the_API_it_was_linked_for_isn_t_linked()
    {
        _tokens.Value = """{"accessToken":"token-xyz","authUrl":"https://www.openstreetmap.org/","clientId":"client-abc","displayName":"Mapper"}""";
        var account = Account();

        await account.LoadAsync();

        Assert.False(account.IsLinked);
    }

    [Theory]
    [InlineData("https://www.openstreetmap.org", "https://attacker.example")]
    [InlineData("https://www.openstreetmap.org", "http://api.openstreetmap.org")]
    [InlineData("https://www.openstreetmap.org", "https://master.apis.dev.openstreetmap.org")]
    [InlineData("https://master.apis.dev.openstreetmap.org", "https://api.openstreetmap.org")]
    [InlineData("http://osm.example", "http://osm.example")]
    public async Task A_server_naming_an_API_that_isn_t_its_sign_in_s_own_never_gets_the_token(string authUrl, string apiUrl)
    {
        // Linked through one server; then the Android app is pointed at another, which names its own API.
        var account = Account();
        Assert.Equal(OsmLinkOutcome.Linked, await account.LinkAsync());
        _config = $$"""{"osmClientId":"client-abc","osmAuthUrl":"{{authUrl}}","osmApiUrl":"{{apiUrl}}"}""";
        _osm.Requests.Clear();
        var other = Account();

        await other.LoadAsync();
        var error = await Assert.ThrowsAsync<OsmEditException>(() => other.SendHoursAsync(Hours, TestContext.Current.CancellationToken));

        Assert.False(other.IsAvailable);
        Assert.False(other.IsLinked);
        Assert.Equal(OsmEditFailure.NotLinked, error.Failure);
        Assert.Equal(OsmLinkOutcome.Failed, await other.LinkAsync());
        Assert.DoesNotContain(_osm.Requests, r => r.Url.Host != "api.test");
        Assert.DoesNotContain(_osm.Requests, r => r.Authorization is not null);
    }

    [Fact]
    public async Task A_server_naming_another_of_OpenStreetMap_s_APIs_asks_for_the_account_again()
    {
        var account = Account();
        await account.LinkAsync();
        _config = """{"osmClientId":"client-abc","osmAuthUrl":"https://www.openstreetmap.org","osmApiUrl":"https://www.openstreetmap.org"}""";
        _osm.Requests.Clear();
        var other = Account();

        await other.LoadAsync();

        Assert.True(other.IsAvailable);
        Assert.False(other.IsLinked);
        await Assert.ThrowsAsync<OsmEditException>(() => other.SendHoursAsync(Hours, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_osm.Requests, r => r.Authorization is not null);
    }

    [Fact]
    public async Task Edits_go_to_the_API_the_account_was_linked_for_with_its_token()
    {
        var account = Account();
        await account.LinkAsync();
        _osm.Requests.Clear();

        await Assert.ThrowsAnyAsync<OsmEditException>(() => account.SendHoursAsync(Hours, TestContext.Current.CancellationToken));

        var get = Assert.Single(_osm.Requests);
        Assert.Equal(("https://api.openstreetmap.org/api/0.6/node/1", "Bearer token-xyz"), (get.Url.AbsoluteUri, get.Authorization));
    }

    [Theory]
    [InlineData("https://www.openstreetmap.org", "https://api.openstreetmap.org", true)]
    [InlineData("https://www.openstreetmap.org/", "https://www.openstreetmap.org", true)]
    [InlineData("https://openstreetmap.org", "https://api.openstreetmap.org", true)]
    [InlineData("https://master.apis.dev.openstreetmap.org", "https://master.apis.dev.openstreetmap.org/", true)]
    [InlineData("http://localhost:3000", "http://localhost:3000", true)]
    [InlineData("http://127.0.0.1:3000", "http://127.0.0.1:3000", true)]
    [InlineData("http://localhost:3000", "http://localhost:3001", false)]
    [InlineData("https://osm.example", "http://osm.example", false)]
    [InlineData("http://osm.example", "http://osm.example", false)]
    [InlineData("https://www.openstreetmap.org", "https://api.openstreetmap.org:8443", false)]
    [InlineData("https://www.openstreetmap.org", "https://api.openstreetmap.org.attacker.example", false)]
    [InlineData("https://master.apis.dev.openstreetmap.org", "https://api.openstreetmap.org", false)]
    public void An_API_belongs_to_its_sign_in_s_server_or_to_OpenStreetMap_s_own_pair(string auth, string api, bool belongs) =>
        Assert.Equal(belongs, OsmServers.ApiBelongsTo(new Uri(auth), new Uri(api)));

    [Fact]
    public async Task Unlinking_forgets_the_token_and_asks_OpenStreetMap_to_revoke_it()
    {
        var account = Account();
        await account.LinkAsync();

        await account.UnlinkAsync();

        Assert.False(account.IsLinked);
        Assert.Equal("", _tokens.Value);
        var revoke = HttpUtility.ParseQueryString(_osm.Requests.Single(r => r.Path == "/oauth2/revoke").Body);
        Assert.Equal(("token-xyz", "client-abc"), (revoke["token"], revoke["client_id"]));
    }

    [Fact]
    public async Task A_token_OpenStreetMap_no_longer_takes_forgets_the_account()
    {
        var account = Account();
        await account.LinkAsync();
        var refusing = new OsmStub(r => r.RequestUri!.Host == "api.test"
            ? Json(ConfigJson)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Couldn't authenticate you", Encoding.UTF8, "text/plain") });
        var editing = new OsmAccount(Api(refusing), new FakeSignIn(_answer), _tokens, new OsmHttp(refusing.Client), new OsmEditor(refusing.Client));
        await editing.LoadAsync();
        Assert.True(editing.IsLinked);

        var error = await Assert.ThrowsAsync<OsmEditException>(() => editing.SendHoursAsync(Hours, TestContext.Current.CancellationToken));

        Assert.Equal((OsmEditFailure.NotLinked, HttpStatusCode.Unauthorized), (error.Failure, error.Status));
        Assert.False(editing.IsLinked);
        Assert.Equal("", _tokens.Value);
    }

    [Fact]
    public void The_links_shown_go_to_the_server_s_OpenStreetMap()
    {
        var settings = OsmSettings.From(new ClientConfig("c", "https://master.apis.dev.openstreetmap.org", "https://master.apis.dev.openstreetmap.org/"))!;

        Assert.Equal("https://master.apis.dev.openstreetmap.org/changeset/42", settings.ChangesetPage(42).AbsoluteUri);
        Assert.Equal("https://master.apis.dev.openstreetmap.org/#map=19/45.5699/-73.7469", settings.MapAt(45.5699, -73.7469).AbsoluteUri);
        Assert.Equal("https://master.apis.dev.openstreetmap.org/edit?relation=789", settings.EditorFor(new OsmElementRef("relation", 789)).AbsoluteUri);
        Assert.Null(OsmSettings.From(new ClientConfig("c", "not a url", "https://api.openstreetmap.org")));
        Assert.Null(OsmSettings.From(new ClientConfig("", "https://www.openstreetmap.org", "https://api.openstreetmap.org")));
    }

    private OsmAccount Account() =>
        new(Api(_osm), new FakeSignIn(_answer), _tokens, new OsmHttp(_osm.Client), new OsmEditor(_osm.Client));

    private static EssenceApiClient Api(OsmStub stub) => new(new HttpClient(stub, disposeHandler: false) { BaseAddress = new Uri("http://api.test/") });

    private static System.Collections.Specialized.NameValueCollection Query(Uri url) => HttpUtility.ParseQueryString(url.Query);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeSignIn(Func<Uri, OsmCallback?> answer) : IOsmSignIn
    {
        public string RedirectUri => Redirect;

        public Task<OsmCallback?> AuthorizeAsync(Uri authorizeUrl, CancellationToken cancellationToken) => Task.FromResult(answer(authorizeUrl));
    }

    private sealed class MemoryTokens : IOsmTokenStore
    {
        public string? Value { get; set; }

        public ValueTask<string?> GetAsync() => ValueTask.FromResult(Value);

        public ValueTask SetAsync(string value)
        {
            Value = value;
            return ValueTask.CompletedTask;
        }
    }
}
