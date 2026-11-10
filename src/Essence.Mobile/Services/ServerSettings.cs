using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Essence.UI.App;
using Essence.UI.Services;

namespace Essence.Mobile.Services;

/// <param name="Root">The address as typed, normalized (<see cref="ServerAddress.Normalize"/>).</param>
/// <param name="Api">Where its API answered (<see cref="ServerAddress.ProbeAsync"/>).</param>
public sealed record SavedServer(Uri Root, Uri Api);

/// <summary>
/// The server the app talks to, chosen at first launch and changeable from About. Each BlazorWebView gets an API client for
/// the server current when it starts; changing server starts a new one (App.ShowMapAsync).
/// </summary>
public sealed class ServerSettings
{
    private const string StorageKey = "essence.server";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly BuildInfo _build;
    private readonly NativeSessionStore _session;
    private readonly AppHttpHandler _handler;

    public ServerSettings(BuildInfo build, NativeSessionStore session, AppHttpHandler handler)
    {
        _build = build;
        _session = session;
        _handler = handler;
        Current = Read();
    }

    /// <summary>Null until one is chosen.</summary>
    public SavedServer? Current { get; private set; }

    /// <summary>The current server as shown, and as the key favorites are kept under (<see cref="ServerScopedPreferences"/>).</summary>
    public string? ServerId => Current is { } server ? ServerAddress.DisplayName(server.Api) : null;

    /// <summary>What the server screen starts with: the current server, else the build's default, as short as it reads.</summary>
    public string Prefill
    {
        get
        {
            var root = Current?.Root
                       ?? (string.IsNullOrEmpty(_build.DefaultServer) ? null : ServerAddress.Normalize(_build.DefaultServer, allowHttp: true).Root);
            return root is null ? "" : Shorten(root);
        }
    }

    /// <summary>Raised after another server is saved.</summary>
    public event Action? Changed;

    /// <summary>Keeps a server that answered. Another server than before drops what the page kept for the old one.</summary>
    public void Save(SavedServer server)
    {
        if (server == Current)
        {
            return;
        }

        var changed = Current is not null;
        Preferences.Default.Set(StorageKey, JsonSerializer.Serialize(server, MobileJsonContext.Default.SavedServer));
        Current = server;
        if (changed)
        {
            // An open trip's stations are the old server's.
            _session.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>A client for the current server's API, one per BlazorWebView scope (MauiProgram).</summary>
    public EssenceApiClient CreateApiClient()
    {
        var server = Current ?? throw new InvalidOperationException("No server is chosen yet.");
        return new EssenceApiClient(new HttpClient(_handler, disposeHandler: false) { BaseAddress = server.Api, Timeout = ApiTimeout });
    }

    /// <summary>A client without a base address, for <see cref="ServerAddress.ProbeAsync"/> and the widgets' own calls.</summary>
    public HttpClient CreateProbeClient() => new(_handler, disposeHandler: false) { Timeout = ProbeTimeout };

    private static SavedServer? Read()
    {
        var json = Preferences.Default.Get<string?>(StorageKey, null);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, MobileJsonContext.Default.SavedServer);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>https://example.com/</c> reads as <c>example.com</c>; anything more stays whole.</summary>
    private static string Shorten(Uri root) =>
        root.Scheme == Uri.UriSchemeHttps && root.AbsolutePath == "/" ? ServerAddress.DisplayName(root) : root.AbsoluteUri;
}

/// <summary>
/// Every request the app makes: says which build asks (<c>Essence-Android/2026.11.07 (456789)</c>), so a server can tell an
/// app too old for it with 426, which <see cref="TooOld"/> reports (MobileAppHost tells the user once).
/// </summary>
public sealed class AppHttpHandler : DelegatingHandler
{
    public AppHttpHandler()
        : base(new HttpClientHandler())
    {
        UserAgent = $"Essence-Android/{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
    }

    public string UserAgent { get; }

    /// <summary>A server answered 426 Upgrade Required.</summary>
    public event Action? TooOld;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.UpgradeRequired)
        {
            TooOld?.Invoke();
        }

        return response;
    }
}

/// <summary>Source-generated (trim-safe) serialization of what the app keeps for itself.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SavedServer))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(KnownRelease))]
internal sealed partial class MobileJsonContext : JsonSerializerContext;
