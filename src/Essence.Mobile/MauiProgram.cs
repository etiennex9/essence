using Essence.Mobile.Services;
using Essence.UI;
using Essence.UI.App;
using Essence.UI.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Essence.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var services = builder.Services;
        services.AddMauiBlazorWebView();
#if DEBUG
        // The page's console and DOM at chrome://inspect.
        services.AddBlazorWebViewDeveloperTools();
#endif

        // Registered before AddEssenceUI, whose browser defaults then stand aside (its TryAdds).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(BuildInfo.Current);
        services.AddSingleton<AppHttpHandler>();
        services.AddSingleton<NativeSessionStore>();
        services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<NativeSessionStore>());
        services.AddSingleton<ServerSettings>();
        services.AddSingleton<MauiPreferenceStore>();
        services.AddSingleton<IPreferenceStore>(sp =>
        {
            var servers = sp.GetRequiredService<ServerSettings>();
            return new ServerScopedPreferences(sp.GetRequiredService<MauiPreferenceStore>(), () => servers.ServerId);
        });
        services.AddSingleton<IDeviceLocation, MauiDeviceLocation>();
        services.AddSingleton<IReleaseNotesSource>(_ => new FileReleaseNotesSource(BundledReleaseNotes.OpenAsync));
        // One per BlazorWebView: a page started after the server changed talks to the new one.
        services.AddScoped(sp => sp.GetRequiredService<ServerSettings>().CreateApiClient());
        services.AddSingleton<AppUpdateCheck>();
        services.AddSingleton<MobileAppHost>();
        services.AddSingleton<IAppHost>(sp => sp.GetRequiredService<MobileAppHost>());
        services.AddSingleton<DeepLinks>();
        // OpenStreetMap's sign-in in a browser tab, its token in the secure storage, and its calls through the app's handler.
        services.AddSingleton<IOsmSignIn, MauiOsmSignIn>();
        services.AddSingleton<IOsmTokenStore, SecureOsmTokenStore>();
        services.AddSingleton(sp => new OsmHttp(
            new HttpClient(sp.GetRequiredService<AppHttpHandler>(), disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddEssenceUI(new EssenceUIOptions());

        return builder.Build();
    }
}

/// <summary>The release notes bundled with the build (Essence.Mobile.csproj's MauiAsset), under release/.</summary>
internal static class BundledReleaseNotes
{
    public static async Task<Stream?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await FileSystem.Current.OpenAppPackageFileAsync("release/" + name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
