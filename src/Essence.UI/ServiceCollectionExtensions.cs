using Essence.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Essence.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the map page needs. The device services default to the browser ones; a host with better ones
    /// (e.g. native geolocation in MAUI) registers its own <see cref="IDeviceLocation"/>, <see cref="IPreferenceStore"/> or
    /// <see cref="ISessionStore"/> first, and its own <see cref="TimeProvider"/> (the web app's simulated time in Development),
    /// and likewise its own <see cref="IReleaseNotesSource"/> (release notes bundled with the app). A host whose API address
    /// isn't known at startup registers its own <see cref="EssenceApiClient"/> and leaves
    /// <see cref="EssenceUIOptions.ApiBaseUrl"/> out. OpenStreetMap's sign-in, where its account is kept and the client it's
    /// called with default to the browser's (<see cref="BrowserOsmSignIn"/>, localStorage, fetch); the Android app registers
    /// its own <see cref="IOsmSignIn"/>, <see cref="IOsmTokenStore"/> and <see cref="OsmHttp"/> first.
    /// </summary>
    public static IServiceCollection AddEssenceUI(this IServiceCollection services, EssenceUIOptions options)
    {
        services.AddSingleton(options);
        if (options.ApiBaseUrl is { } apiUrl)
        {
            var apiBaseUrl = WithTrailingSlash(apiUrl);
            services.TryAddSingleton(_ => new EssenceApiClient(new HttpClient { BaseAddress = apiBaseUrl, Timeout = TimeSpan.FromSeconds(20) }));
        }
        else if (!services.Any(s => s.ServiceType == typeof(EssenceApiClient)))
        {
            throw new InvalidOperationException(
                $"Give {nameof(EssenceUIOptions)}.{nameof(EssenceUIOptions.ApiBaseUrl)}, or register an {nameof(EssenceApiClient)} first.");
        }

        services.AddLocalization();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<HistoryCache>();
        services.AddScoped<HistoryFuels>();
        services.AddScoped<FavoriteStations>();
        services.AddScoped<ComparedStations>();
        services.AddScoped<CarProfiles>();
        services.AddScoped<BestNearYou>();
        services.AddScoped<SavedPlaces>();
        services.AddScoped<TripState>();
        services.AddScoped<PlaceSearch>();
        services.AddScoped<PlaceAddresses>();
        services.AddScoped<ServicesStatus>();
        services.AddScoped<StationHoursOverrides>();
        services.AddScoped<StationHoursView>();
        services.TryAddScoped<BrowserDevice>();
        services.TryAddScoped<IDeviceLocation>(sp => sp.GetRequiredService<BrowserDevice>());
        services.TryAddScoped<IPreferenceStore>(sp => sp.GetRequiredService<BrowserDevice>());
        services.TryAddScoped<ISessionStore>(sp => sp.GetRequiredService<BrowserDevice>().Session);
        services.TryAddScoped<IOsmSignIn, BrowserOsmSignIn>();
        services.TryAddScoped<IOsmTokenStore, PreferenceOsmTokenStore>();
        services.TryAddSingleton(_ => new OsmHttp(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddScoped(sp => new OsmEditor(sp.GetRequiredService<OsmHttp>().Client));
        services.AddScoped<OsmAccount>();
        if (options.ReleaseNotesUrl is { } notesUrl)
        {
            var notesBaseUrl = WithTrailingSlash(notesUrl);
            services.TryAddSingleton<IReleaseNotesSource>(
                _ => new HttpReleaseNotesSource(new HttpClient { BaseAddress = notesBaseUrl, Timeout = TimeSpan.FromSeconds(20) }));
        }

        services.TryAddSingleton<IReleaseNotesSource, NoReleaseNotes>();
        services.AddScoped<ReleaseNotes>();
        services.AddScoped<UpdateCheck>();
        return services;
    }

    // API paths are relative to its root, so the base address must end with a slash.
    private static Uri WithTrailingSlash(Uri url) => url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
}
