using System.Net;
using System.Text.Json;
using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.App;

public enum ServerAddressError
{
    None,
    Empty,

    /// <summary>Not a web address.</summary>
    Invalid,

    /// <summary>An http:// address where only https:// is allowed.</summary>
    HttpsRequired,
}

public enum ServerProbeError
{
    None,

    /// <summary>No answer: a wrong address, or no connection.</summary>
    Unreachable,

    /// <summary>It answered, but not as an Essence server.</summary>
    NotEssence,

    /// <summary>It's an Essence server that no longer serves this build of the app (426): a newer one is needed.</summary>
    AppTooOld,
}

/// <param name="Root">The address with a scheme and a trailing slash, when it's valid.</param>
public sealed record NormalizedServer(Uri? Root, ServerAddressError Error);

/// <param name="ApiBase">Where the API answered: <c>{root}api/</c> behind the web app's nginx, or the root itself.</param>
public sealed record ServerProbe(Uri? ApiBase, ServerProbeError Error);

/// <summary>
/// The server address the Android app asks for at first launch. People type a website's address (<c>example.com</c>); the
/// app talks to its API, under <c>api/</c> (README → Deploy), or straight to an API's own root in development.
/// </summary>
public static class ServerAddress
{
    /// <summary>
    /// The typed address as an absolute https URL ending in a slash: <c>https://</c> is added when no scheme is given, and
    /// a query or fragment is dropped. <paramref name="allowHttp"/> also accepts http:// (development builds).
    /// </summary>
    public static NormalizedServer Normalize(string? typed, bool allowHttp)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0)
        {
            return new NormalizedServer(null, ServerAddressError.Empty);
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0
            || uri.Host.Contains(' ', StringComparison.Ordinal))
        {
            return new NormalizedServer(null, ServerAddressError.Invalid);
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp)
        {
            return new NormalizedServer(null, ServerAddressError.HttpsRequired);
        }

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        var root = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, path).Uri;
        return new NormalizedServer(root, ServerAddressError.None);
    }

    /// <summary>How the server is shown: its host, with the port when it isn't the scheme's default.</summary>
    public static string DisplayName(Uri root) => root.IsDefaultPort ? root.Host : $"{root.Host}:{root.Port}";

    /// <summary>
    /// Finds the API behind <paramref name="root"/>: <c>{root}api/</c> first, then <c>{root}</c>. A candidate counts when its
    /// <c>services</c> endpoint answers with Essence's JSON, or asks to slow down (429, which only Essence.Api's limiter
    /// says here). <paramref name="http"/> has no base address; its timeout applies to each request.
    /// </summary>
    public static async Task<ServerProbe> ProbeAsync(Uri root, HttpClient http, CancellationToken cancellationToken)
    {
        var anyAnswer = false;
        foreach (var candidate in new[] { new Uri(root, "api/"), root })
        {
            switch (await AskAsync(new Uri(candidate, "services"), http, cancellationToken))
            {
                case Answer.Essence:
                    return new ServerProbe(candidate, ServerProbeError.None);
                case Answer.TooOld:
                    return new ServerProbe(null, ServerProbeError.AppTooOld);
                case Answer.Other:
                    anyAnswer = true;
                    break;
            }
        }

        return new ServerProbe(null, anyAnswer ? ServerProbeError.NotEssence : ServerProbeError.Unreachable);
    }

    private enum Answer
    {
        None,
        Other,
        Essence,
        TooOld,
    }

    private static async Task<Answer> AskAsync(Uri url, HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Answer.Essence;
            }

            if (response.StatusCode == HttpStatusCode.UpgradeRequired)
            {
                return Answer.TooOld;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Answer.Other;
            }

            // Any JSON object would read as a status with nulls; Essence's names both services.
            var status = await JsonSerializer.DeserializeAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), EssenceJsonContext.Default.DataServicesStatus, cancellationToken);
            return status is { Routing: not null, Geocoding: not null } ? Answer.Essence : Answer.Other;
        }
        catch (JsonException)
        {
            return Answer.Other;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A timeout surfaces as TaskCanceledException without our token being cancelled.
            return Answer.None;
        }
    }
}
