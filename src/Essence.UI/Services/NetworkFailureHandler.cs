using System.Net;

namespace Essence.UI.Services;

/// <summary>
/// Reports a server that can't be reached as <see cref="HttpRequestException"/>, as the browser's fetch and .NET's own handler
/// do, so that every caller's catch sees it (<see cref="EssenceApiClient"/>'s <see cref="ApiError.Failed"/>). Android's handler
/// throws <see cref="WebException"/> instead (a closed port, <c>adb reverse</c> with nothing behind it: "Socket closed",
/// "unexpected end of stream"), or <see cref="IOException"/>; uncaught, a page showed Blazor's "Something went wrong". The
/// Android app's handler sends through it (AppHttpHandler).
/// </summary>
public sealed class NetworkFailureHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is WebException or IOException)
        {
            // Failing because it was cancelled (the caller moved on, or HttpClient's timeout) stays a cancellation:
            // HttpClient takes an HttpRequestException after its token was cancelled for one, and tells which.
            throw new HttpRequestException(ex.Message, ex);
        }
    }
}
