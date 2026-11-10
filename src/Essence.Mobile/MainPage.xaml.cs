using AndroidX.Activity;
using Essence.Mobile.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WebView = Android.Webkit.WebView;

namespace Essence.Mobile;

/// <summary>
/// The map page in a BlazorWebView. Back goes back through the page's history (the BlazorWebView does that itself), which
/// closes its panes as on the web; a dialog open over the page closes first (<see cref="DialogBack"/>).
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly DialogBackCallback _dialogBack = new();

    public MainPage(string startPath)
    {
        InitializeComponent();
        blazorWebView.StartPath = startPath;
        blazorWebView.HandlerChanged += OnWebViewHandlerChanged;
        Current = this;
    }

    /// <summary>The page shown, for navigation and Back from native code.</summary>
    public static MainPage? Current { get; private set; }

    private WebView? PlatformWebView => blazorWebView.Handler?.PlatformView as WebView;

    /// <summary>Opens a path of the map page (a widget's tap). False when Blazor isn't running yet.</summary>
    public Task<bool> TryNavigateAsync(string path) =>
        blazorWebView.TryDispatchAsync(services => services.GetRequiredService<NavigationManager>().Open(path));

    /// <summary>
    /// The app went to the background: the WebView pauses, so the page sees itself hidden (visibilitychange) and its refreshes
    /// stop, as in a browser tab in the background.
    /// </summary>
    public void OnAppStopped() => PlatformWebView?.OnPause();

    public void OnAppResumed() => PlatformWebView?.OnResume();

    /// <summary>Lets go of the WebView, which disposes the page's scope and stops its timers.</summary>
    public void Detach()
    {
        if (Current == this)
        {
            Current = null;
        }

        _dialogBack.Remove();
        blazorWebView.HandlerChanged -= OnWebViewHandlerChanged;
        blazorWebView.Handler?.DisconnectHandler();
    }

    /// <summary>
    /// Registers the dialog's Back after the BlazorWebView has registered its own, so that it's asked first (Android asks the
    /// newest first). It's enabled only while a dialog is open, so Back's preview of going home still works otherwise.
    /// </summary>
    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
        _dialogBack.Remove();
        if (PlatformWebView is { } webView && Platform.CurrentActivity is ComponentActivity activity)
        {
            _dialogBack.WebView = webView;
            activity.OnBackPressedDispatcher.AddCallback(activity, _dialogBack);
        }
    }

    internal static void SetDialogOpen(bool open)
    {
        if (Current is { } page)
        {
            page._dialogBack.Enabled = open;
        }
    }

    private sealed class DialogBackCallback() : OnBackPressedCallback(false)
    {
        public WebView? WebView { get; set; }

        public override void HandleOnBackPressed() => WebView?.EvaluateJavascript("essenceHost.closeDialog()", null);
    }
}

/// <summary>essenceHost.js says when a dialog opens or closes over the page.</summary>
public static class DialogBack
{
    [JSInvokable("DialogOpenChanged")]
    public static void DialogOpenChanged(bool open) => MainThread.BeginInvokeOnMainThread(() => MainPage.SetDialogOpen(open));
}
