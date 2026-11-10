using Essence.Mobile.Services;
using Essence.UI.App;
using Essence.UI.Resources;
using Microsoft.Extensions.Localization;

namespace Essence.Mobile;

/// <summary>
/// Asks which server to use: at first launch (prefilled with the build's default, README → Versions) and from About →
/// Server → Change. The address is checked before anything is kept (<see cref="ServerAddress"/>).
/// </summary>
public sealed class ServerPage : ContentPage
{
#if DEBUG
    // Development builds reach the local API through adb reverse, over http.
    private const bool AllowHttp = true;
#else
    private const bool AllowHttp = false;
#endif

    private readonly ServerSettings _servers;
    private readonly IStringLocalizer<Strings> _l;
    private readonly Entry _address;
    private readonly Label _error;
    private readonly Button _connect;
    private readonly ActivityIndicator _busy;
    private CancellationTokenSource? _probe;

    public ServerPage(ServerSettings servers, IStringLocalizer<Strings> l, bool changing)
    {
        _servers = servers;
        _l = l;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.All);
        this.SetAppThemeColor(BackgroundColorProperty, Colors.Bg.Light, Colors.Bg.Dark);

        _address = new Entry
        {
            Text = servers.Prefill,
            Placeholder = "example.com",
            Keyboard = Keyboard.Url,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            ReturnType = ReturnType.Go,
            FontSize = 17,
        };
        _address.SetAppThemeColor(Entry.TextColorProperty, Colors.Text.Light, Colors.Text.Dark);
        _address.SetAppThemeColor(Entry.PlaceholderColorProperty, Colors.Muted.Light, Colors.Muted.Dark);
        _address.Completed += (_, _) => _ = ConnectAsync();
        _address.TextChanged += (_, _) => _error!.IsVisible = false;
        SemanticProperties.SetDescription(_address, l["ServerAddressLabel"]);

        _error = new Label { IsVisible = false, FontSize = 15 };
        _error.SetAppThemeColor(Label.TextColorProperty, Colors.Warning.Light, Colors.Warning.Dark);
        SemanticProperties.SetHeadingLevel(_error, SemanticHeadingLevel.None);

        _connect = new Button { Text = l["ServerConnect"], FontSize = 16, CornerRadius = 22, HeightRequest = 48 };
        _connect.SetAppThemeColor(Button.BackgroundColorProperty, Colors.Accent.Light, Colors.Accent.Dark);
        _connect.SetAppThemeColor(Button.TextColorProperty, Colors.OnAccent.Light, Colors.OnAccent.Dark);
        _connect.Clicked += (_, _) => _ = ConnectAsync();

        _busy = new ActivityIndicator { IsVisible = false, IsRunning = false, HeightRequest = 24 };
        _busy.SetAppThemeColor(ActivityIndicator.ColorProperty, Colors.Accent.Light, Colors.Accent.Dark);

        var title = new Label { Text = l["ServerTitle"], FontSize = 26, FontAttributes = FontAttributes.Bold };
        title.SetAppThemeColor(Label.TextColorProperty, Colors.Text.Light, Colors.Text.Dark);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);

        var intro = new Label { Text = l["ServerIntro"], FontSize = 16 };
        intro.SetAppThemeColor(Label.TextColorProperty, Colors.Muted.Light, Colors.Muted.Dark);

        var label = new Label { Text = l["ServerAddressLabel"], FontSize = 14, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 12, 0, 0) };
        label.SetAppThemeColor(Label.TextColorProperty, Colors.Muted.Light, Colors.Muted.Dark);

        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 48, 24, 24), MaximumWidthRequest = 520 };
        stack.Add(title);
        stack.Add(intro);
        stack.Add(label);
        stack.Add(_address);
        stack.Add(_error);
        stack.Add(_connect);
        stack.Add(_busy);
        if (changing)
        {
            var cancel = new Button { Text = l["ServerCancel"], BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent, FontSize = 16 };
            cancel.SetAppThemeColor(Button.TextColorProperty, Colors.Accent.Light, Colors.Accent.Dark);
            cancel.Clicked += async (_, _) =>
            {
                // Once: a second tap, or the probe's answer arriving meanwhile, would pop the map's page instead.
                cancel.IsEnabled = _connect.IsEnabled = false;
                _probe?.Cancel();
                if (Navigation.ModalStack.LastOrDefault() == this)
                {
                    await Navigation.PopModalAsync();
                }
            };
            stack.Add(cancel);
        }

        Content = new ScrollView { Content = stack };
    }

    protected override void OnDisappearing()
    {
        _probe?.Cancel();
        base.OnDisappearing();
    }

    private async Task ConnectAsync()
    {
        if (_probe is not null)
        {
            return;
        }

        var normalized = ServerAddress.Normalize(_address.Text, AllowHttp);
        if (normalized.Root is not { } root)
        {
            ShowError(normalized.Error switch
            {
                ServerAddressError.Empty => "ServerErrorEmpty",
                ServerAddressError.HttpsRequired => "ServerErrorHttps",
                _ => "ServerErrorInvalid",
            });
            return;
        }

        using var probe = _probe = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            using var http = _servers.CreateProbeClient();
            var result = await ServerAddress.ProbeAsync(root, http, probe.Token);
            probe.Token.ThrowIfCancellationRequested();
            if (result.ApiBase is not { } api)
            {
                ShowError(result.Error switch
                {
                    ServerProbeError.NotEssence => "ServerErrorNotEssence",
                    ServerProbeError.AppTooOld => "ServerErrorTooOld",
                    _ => "ServerErrorUnreachable",
                });
                return;
            }

            var server = new SavedServer(root, api);
            var changed = server != _servers.Current;
            _servers.Save(server);
            Widgets.WidgetUpdates.RefreshNow(Platform.AppContext);
            await App.Current.ShowMapAsync(newPage: changed);
        }
        catch (OperationCanceledException)
        {
            // Cancelled or left.
        }
        finally
        {
            _probe = null;
            if (!probe.IsCancellationRequested)
            {
                SetBusy(false);
            }
        }
    }

    private void ShowError(string key)
    {
        _error.Text = _l[key];
        _error.IsVisible = true;
        SemanticScreenReader.Announce(_error.Text);
    }

    private void SetBusy(bool busy)
    {
        _connect.IsEnabled = !busy;
        _connect.Text = busy ? _l["ServerConnecting"] : _l["ServerConnect"];
        _busy.IsVisible = _busy.IsRunning = busy;
    }

    /// <summary>essence.css's colors, light and dark.</summary>
    private static class Colors
    {
        public static readonly (Color Light, Color Dark) Bg = (Color.FromArgb("#F2F0EA"), Color.FromArgb("#1B1B1A"));
        public static readonly (Color Light, Color Dark) Text = (Color.FromArgb("#1D1D1B"), Color.FromArgb("#F1EFE8"));
        public static readonly (Color Light, Color Dark) Muted = (Color.FromArgb("#5F5E5A"), Color.FromArgb("#B4B2A9"));
        public static readonly (Color Light, Color Dark) Accent = (Color.FromArgb("#185FA5"), Color.FromArgb("#85B7EB"));
        public static readonly (Color Light, Color Dark) OnAccent = (Color.FromArgb("#FFFFFF"), Color.FromArgb("#042C53"));
        public static readonly (Color Light, Color Dark) Warning = (Color.FromArgb("#633806"), Color.FromArgb("#FAC775"));
    }
}
