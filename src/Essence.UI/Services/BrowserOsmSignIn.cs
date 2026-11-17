using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Essence.UI.Services;

/// <summary>
/// OpenStreetMap's sign-in for the web app (<see cref="IOsmSignIn"/>): a window of its own (<c>essenceOsm.js</c>), whose
/// answer comes back to <c>osm-auth.html</c> next to the page, which hands the code and the state back to it. Opened from
/// the click that asked for it, so popup blockers let it through.
/// </summary>
public sealed class BrowserOsmSignIn(IJSRuntime js, NavigationManager navigation) : IOsmSignIn, IAsyncDisposable
{
    private const string ModulePath = "./_content/Essence.UI/js/essenceOsm.js";

    private Task<IJSObjectReference>? _module;

    /// <summary>The page's <c>osm-auth.html</c>: <c>https://&lt;your domain&gt;/osm-auth.html</c>.</summary>
    public string RedirectUri => navigation.BaseUri + "osm-auth.html";

    public async Task<OsmCallback?> AuthorizeAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        // Imported ahead by Prepare: an import here, after the click, could cost the window its user gesture.
        var module = await GetModuleAsync();
        await using var cancel = cancellationToken.Register(() => _ = CancelAsync(module));
        try
        {
            var answer = await module.InvokeAsync<JsAnswer?>(
                "authorize", CancellationToken.None, authorizeUrl.AbsoluteUri, (int)OsmAccount.SignInTimeout.TotalMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            return answer is null ? null : new OsmCallback(answer.Code, answer.State, answer.Error);
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
    }

    /// <summary>Loads the script before it's needed (the editor and About call it as they open).</summary>
    public async Task PrepareAsync()
    {
        try
        {
            await GetModuleAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            // Tried again at the sign-in.
            _module = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await (await _module).DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The page is going away.
        }
    }

    private static async Task CancelAsync(IJSObjectReference module)
    {
        try
        {
            await module.InvokeVoidAsync("cancel");
        }
        catch (JSDisconnectedException)
        {
            // The page is going away, and the sign-in with it.
        }
    }

    private Task<IJSObjectReference> GetModuleAsync() => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private sealed record JsAnswer(string? Code, string? State, string? Error);
}
