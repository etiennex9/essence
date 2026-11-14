using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>The OpenStreetMap application and servers the Essence server names (<c>/client-config</c>).</summary>
/// <param name="AuthUrl">Where its users sign in, and its pages are: <c>https://www.openstreetmap.org/</c>.</param>
/// <param name="ApiUrl">Where edits go: <c>https://api.openstreetmap.org/</c>.</param>
public sealed record OsmSettings(string ClientId, Uri AuthUrl, Uri ApiUrl)
{
    /// <summary>
    /// The server's settings, or null when editing OpenStreetMap is off (no client id) or they can't be used: not https (but
    /// on the device itself), or an API that isn't the sign-in's own (<see cref="OsmServers"/>). The app doesn't take the
    /// server's word for where a token goes: the Android app's server can be anyone's.
    /// </summary>
    public static OsmSettings? From(ClientConfig? config) =>
        config is { OsmClientId: { Length: > 0 } clientId } && Root(config.OsmAuthUrl) is { } auth && Root(config.OsmApiUrl) is { } api
        && OsmServers.ApiBelongsTo(auth, api)
            ? new OsmSettings(clientId, auth, api)
            : null;

    /// <summary>A changeset's page: <c>https://www.openstreetmap.org/changeset/123</c>.</summary>
    public Uri ChangesetPage(long id) => new(AuthUrl, string.Create(CultureInfo.InvariantCulture, $"changeset/{id}"));

    /// <summary>The map at a spot, close enough to add a place there: <c>https://www.openstreetmap.org/#map=19/45.5/-73.6</c>.</summary>
    public Uri MapAt(double latitude, double longitude) =>
        new(AuthUrl, string.Create(CultureInfo.InvariantCulture, $"#map=19/{latitude:0.######}/{longitude:0.######}"));

    /// <summary>OpenStreetMap's editor on an element: <c>https://www.openstreetmap.org/edit?relation=789</c>.</summary>
    public Uri EditorFor(OsmElementRef element) =>
        new(AuthUrl, string.Create(CultureInfo.InvariantCulture, $"edit?{element.Type}={element.Id}"));

    /// <summary>A safe address (<see cref="OsmServers.IsSafe"/>) with a trailing slash, so relative paths go under it.</summary>
    private static Uri? Root(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var url) && OsmServers.IsSafe(url)
            ? new Uri(url.AbsoluteUri.TrimEnd('/') + "/")
            : null;
}

/// <summary>OpenStreetMap's answer to its sign-in, as the redirect brought it back: a code and its state, or an error.</summary>
/// <param name="Error">OAuth's <c>error</c> (<c>access_denied</c> when the user declined), or <see cref="PopupBlocked"/>.</param>
public sealed record OsmCallback(string? Code, string? State, string? Error = null)
{
    /// <summary>The browser didn't let the sign-in's window open.</summary>
    public const string PopupBlocked = "popup_blocked";
}

/// <summary>
/// How the host shows OpenStreetMap's sign-in and gets its answer back: the web app in a window of its own, whose page
/// (<c>osm-auth.html</c>) hands the code back; the Android app in a browser tab, back through <c>essence://osm-auth</c>.
/// </summary>
public interface IOsmSignIn
{
    /// <summary>Where OpenStreetMap sends its answer, exactly as the OAuth application has it registered: it compares them as text.</summary>
    string RedirectUri { get; }

    /// <summary>Opens <paramref name="authorizeUrl"/> and waits for the answer; null when the user gave up.</summary>
    Task<OsmCallback?> AuthorizeAsync(Uri authorizeUrl, CancellationToken cancellationToken);

    /// <summary>Gets ready before the click that signs in, so nothing slow stands between the two (the web's popup blocker).</summary>
    Task PrepareAsync() => Task.CompletedTask;
}

/// <summary>
/// Where the device keeps its linked OpenStreetMap account, its token above all, which the Essence server never sees. Not
/// per Essence server: the account is the person's. Android keeps it in its secure storage; the web app in localStorage
/// (<see cref="PreferenceOsmTokenStore"/>).
/// </summary>
public interface IOsmTokenStore
{
    ValueTask<string?> GetAsync();

    /// <summary>Keeps the value; "" forgets it.</summary>
    ValueTask SetAsync(string value);
}

/// <summary>The account in the page's preferences (localStorage on the web) under a key of its own, never per server.</summary>
public sealed class PreferenceOsmTokenStore(IPreferenceStore preferences) : IOsmTokenStore
{
    public const string StorageKey = "essence.osm";

    public ValueTask<string?> GetAsync() => preferences.GetAsync(StorageKey);

    public ValueTask SetAsync(string value) => preferences.SetAsync(StorageKey, value);
}

/// <summary>
/// The <see cref="HttpClient"/> OpenStreetMap is called with, absolute addresses only: a host may register its own first
/// (the Android app's says which build asks).
/// </summary>
public sealed class OsmHttp(HttpClient client)
{
    public HttpClient Client { get; } = client;
}

/// <summary>
/// OAuth 2's authorization code with PKCE (RFC 7636), as a public client: no secret, so what proves the token request
/// comes from whoever started the sign-in is the verifier, whose SHA-256 went with it; the state ties the answer to it.
/// </summary>
public static class OsmOAuth
{
    /// <summary>Edits, and the user's name to say who's linked.</summary>
    public const string Scopes = "write_api read_prefs";

    /// <summary>43 characters from 32 random bytes (base64url), as RFC 7636 asks.</summary>
    public static string NewVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The challenge sent for a verifier: its SHA-256, base64url without padding (S256).</summary>
    public static string ChallengeOf(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The answer carries the state the sign-in was started with, compared in constant time.</summary>
    public static bool StateMatches(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    /// <summary>Where the sign-in starts: <c>{auth}/oauth2/authorize?response_type=code&amp;…&amp;code_challenge_method=S256</c>.</summary>
    public static Uri AuthorizeUrl(OsmSettings settings, string redirectUri, string state, string challenge) =>
        new(settings.AuthUrl, "oauth2/authorize?" + Query(
            ("response_type", "code"),
            ("client_id", settings.ClientId),
            ("redirect_uri", redirectUri),
            ("scope", Scopes),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256")));

    private static string Query(params (string Name, string Value)[] parameters) =>
        string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
}

/// <summary>How linking an account went.</summary>
public enum OsmLinkOutcome
{
    Linked,

    /// <summary>The user gave up, or declined.</summary>
    Cancelled,

    /// <summary>The browser didn't let the sign-in's window open.</summary>
    Blocked,

    Failed,
}

/// <summary>
/// The OpenStreetMap account this device linked, if any, to send the hours the user corrects there too, in their name:
/// OpenStreetMap's rules want edits from the person who makes them, so there's no shared account. Linked with OAuth 2 and
/// PKCE (<see cref="OsmOAuth"/>), from the app itself; the token stays on the device (<see cref="IOsmTokenStore"/>), and
/// the Essence server only names the application (<c>/client-config</c>). Off when the server names none.
/// </summary>
public sealed class OsmAccount(EssenceApiClient api, IOsmSignIn signIn, IOsmTokenStore store, OsmHttp http, OsmEditor editor)
{
    /// <summary>How long the sign-in may take before it's given up.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    private Task? _loading;
    private bool _settingsKnown;
    private Link? _link;
    private CancellationTokenSource? _linkCts;

    /// <summary>Raised when the settings come in, and when the account is linked, unlinked or being linked.</summary>
    public event Action? Changed;

    /// <summary>The server's OpenStreetMap application; null until loaded, and when editing OpenStreetMap is off.</summary>
    public OsmSettings? Settings { get; private set; }

    /// <summary>Editing OpenStreetMap is on, on this server.</summary>
    public bool IsAvailable => Settings is not null;

    /// <summary>
    /// An account is linked, for this server's OpenStreetMap: the one it signed in at, with the API it was linked for. The
    /// account isn't per Essence server, but its token only ever goes where it was linked: a server naming another API (on
    /// Android, any server can be picked) asks for the account to be linked again.
    /// </summary>
    public bool IsLinked =>
        _link is not null && Settings is { } settings
        && _link.AuthUrl == settings.AuthUrl.AbsoluteUri && _link.ApiUrl == settings.ApiUrl.AbsoluteUri;

    /// <summary>Its user name, when OpenStreetMap gave it.</summary>
    public string? DisplayName => IsLinked ? _link!.DisplayName : null;

    /// <summary>The sign-in is open: the user is still at OpenStreetMap.</summary>
    public bool Linking { get; private set; }

    /// <summary>Reads the server's settings and the account kept, once; a failure to get the settings is tried again next time.</summary>
    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: true } && !_settingsKnown)
        {
            _loading = null;
        }

        return _loading ??= LoadCoreAsync();
    }

    /// <summary>
    /// Links an account: OpenStreetMap's sign-in (<see cref="IOsmSignIn"/>), the state checked, the code traded for a token
    /// with the verifier, the user's name asked for, and the lot kept on the device.
    /// </summary>
    public async Task<OsmLinkOutcome> LinkAsync()
    {
        await LoadAsync();
        if (Settings is not { } settings || Linking)
        {
            return OsmLinkOutcome.Failed;
        }

        var verifier = OsmOAuth.NewVerifier();
        var state = OsmOAuth.NewState();
        var redirect = signIn.RedirectUri;
        var url = OsmOAuth.AuthorizeUrl(settings, redirect, state, OsmOAuth.ChallengeOf(verifier));
        using var cts = _linkCts = new CancellationTokenSource(SignInTimeout);
        Linking = true;
        Changed?.Invoke();
        try
        {
            var callback = await signIn.AuthorizeAsync(url, cts.Token);
            if (callback is null || callback.Error == "access_denied")
            {
                return OsmLinkOutcome.Cancelled;
            }

            if (callback.Error == OsmCallback.PopupBlocked)
            {
                return OsmLinkOutcome.Blocked;
            }

            // An answer to another sign-in than this one, or none at all, is never traded for a token.
            if (callback.Error is not null || callback.Code is not { Length: > 0 } code || !OsmOAuth.StateMatches(state, callback.State))
            {
                return OsmLinkOutcome.Failed;
            }

            if (await TokenAsync(settings, code, redirect, verifier, cts.Token) is not { } token)
            {
                return OsmLinkOutcome.Failed;
            }

            _link = new Link(
                token, settings.AuthUrl.AbsoluteUri, settings.ApiUrl.AbsoluteUri, settings.ClientId, await DisplayNameAsync(settings, token, cts.Token));
            await store.SetAsync(JsonSerializer.Serialize(_link, OsmJsonContext.Default.Link));
            return OsmLinkOutcome.Linked;
        }
        catch (OperationCanceledException)
        {
            return OsmLinkOutcome.Cancelled;
        }
        finally
        {
            _linkCts = null;
            Linking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Gives up the sign-in that's open.</summary>
    public void CancelLink() => _linkCts?.Cancel();

    /// <summary>Forgets the account on the device, and asks OpenStreetMap to revoke its token (if it can't, it's forgotten all the same).</summary>
    public async Task UnlinkAsync()
    {
        await LoadAsync();
        if (_link is not { } link)
        {
            return;
        }

        _link = null;
        await store.SetAsync("");
        Changed?.Invoke();
        await RevokeAsync(link);
    }

    /// <summary>
    /// Sends a correction of a station's hours (<see cref="OsmEditor"/>), with the linked account, to the API it was linked
    /// for; returns its changeset. A token OpenStreetMap no longer takes (401) forgets the account, to be linked again.
    /// </summary>
    /// <exception cref="OsmEditException">OpenStreetMap refused it, or can't be reached, or no account is linked for it.</exception>
    public async Task<long> SendHoursAsync(OsmHoursEdit edit, CancellationToken cancellationToken)
    {
        await LoadAsync();
        if (!IsLinked || _link is not { } link)
        {
            throw new OsmEditException(OsmEditFailure.NotLinked, "No OpenStreetMap account is linked.");
        }

        try
        {
            return await editor.UpdateHoursAsync(new Uri(link.ApiUrl), link.AccessToken, edit, cancellationToken);
        }
        catch (OsmEditException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            _link = null;
            await store.SetAsync("");
            Changed?.Invoke();
            throw;
        }
    }

    /// <summary>
    /// The element's tags as this server's OpenStreetMap has them now (<see cref="OsmEditor.ReadTagsAsync"/>), to name it in
    /// the hours editor: read from the API the server names, linked or not, since reading needs no token, and none is sent.
    /// Null when editing OpenStreetMap is off, or they can't be had.
    /// </summary>
    public async Task<OsmElementTags?> ReadTagsAsync(OsmElementRef element, CancellationToken cancellationToken)
    {
        await LoadAsync();
        return Settings is { } settings ? await editor.ReadTagsAsync(settings.ApiUrl, element, cancellationToken) : null;
    }

    private async Task LoadCoreAsync()
    {
        var prepare = signIn.PrepareAsync();
        var config = await api.GetClientConfigAsync(CancellationToken.None);
        await prepare;
        // An older server has no settings to give: it won't have any later either.
        _settingsKnown = config.IsSuccess || config.Error == ApiError.NotFound;
        Settings = OsmSettings.From(config.Value);
        if (_link is null && await store.GetAsync() is { Length: > 0 } json)
        {
            try
            {
                _link = JsonSerializer.Deserialize(json, OsmJsonContext.Default.Link) is { AccessToken.Length: > 0, AuthUrl.Length: > 0, ApiUrl.Length: > 0 } link
                    ? link
                    : null;
            }
            catch (JsonException)
            {
                _link = null;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The code traded for a token at <c>{auth}/oauth2/token</c>; null when OpenStreetMap refuses it.</summary>
    private async Task<string?> TokenAsync(OsmSettings settings, string code, string redirect, string verifier, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = settings.ClientId,
            ["code_verifier"] = verifier,
        });
        try
        {
            using var response = await http.Client.PostAsync(new Uri(settings.AuthUrl, "oauth2/token"), content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return answer.RootElement.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String
                   && token.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>The user's name (<c>/api/0.6/user/details.json</c>), to say who's linked; null when it can't be had.</summary>
    private async Task<string?> DisplayNameAsync(OsmSettings settings, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.ApiUrl, "api/0.6/user/details.json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return answer.RootElement.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                   && user.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Asks OpenStreetMap to revoke the token (<c>{auth}/oauth2/revoke</c>), as best it can.</summary>
    private async Task RevokeAsync(Link link)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = link.AccessToken, ["client_id"] = link.ClientId });
        try
        {
            using var response = await http.Client.PostAsync(new Uri(new Uri(link.AuthUrl), "oauth2/revoke"), content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // Forgotten on the device all the same; the user can revoke it in OpenStreetMap's settings.
        }
    }

    /// <summary>The account as the device keeps it.</summary>
    /// <param name="AuthUrl">The OpenStreetMap it's for (<see cref="OsmSettings.AuthUrl"/>): a development server's token is no good on the real one.</param>
    /// <param name="ApiUrl">The API it was linked for (<see cref="OsmSettings.ApiUrl"/>), the only one its token goes to.</param>
    /// <param name="ClientId">The application it was given to, which revokes it.</param>
    internal sealed record Link(string AccessToken, string AuthUrl, string ApiUrl, string ClientId, string? DisplayName);
}

/// <summary>Source-generated (trim-safe) serialization of the linked account.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OsmAccount.Link))]
internal sealed partial class OsmJsonContext : JsonSerializerContext;
