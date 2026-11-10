using Essence.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Essence.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the map page needs. The device services default to the browser ones; a host with better ones
    /// (e.g. native geolocation in MAUI) registers its own <see cref="IDeviceLocation"/>, <see cref="IPreferenceStore"/> or
    /// <see cref="ISessionStore"/> first,
    /// and likewise its own <see cref="IReleaseNotesSource"/> (release notes bundled with the app). A host whose API address
    /// isn't known at startup registers its own <see cref="EssenceApiClient"/> and leaves
    /// <see cref="EssenceUIOptions.ApiBaseUrl"/> out.
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
        services.TryAddScoped<BrowserDevice>();
        services.TryAddScoped<IDeviceLocation>(sp => sp.GetRequiredService<BrowserDevice>());
        services.TryAddScoped<IPreferenceStore>(sp => sp.GetRequiredService<BrowserDevice>());
        services.TryAddScoped<ISessionStore>(sp => sp.GetRequiredService<BrowserDevice>().Session);
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
