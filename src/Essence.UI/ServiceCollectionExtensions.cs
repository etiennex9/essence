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
    /// and likewise its own <see cref="IReleaseNotesSource"/> (release notes bundled with the app).
    /// </summary>
    public static IServiceCollection AddEssenceUI(this IServiceCollection services, EssenceUIOptions options)
    {
        // API paths are relative to its root, so the base address must end with a slash.
        var apiBaseUrl = options.ApiBaseUrl.AbsoluteUri.EndsWith('/') ? options.ApiBaseUrl : new Uri(options.ApiBaseUrl.AbsoluteUri + "/");

        services.AddSingleton(options);
        services.AddSingleton(_ => new EssenceApiClient(new HttpClient { BaseAddress = apiBaseUrl, Timeout = TimeSpan.FromSeconds(20) }));
        services.AddLocalization();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<HistoryCache>();
        services.AddScoped<FavoriteStations>();
        services.AddScoped<ComparedStations>();
        services.AddScoped<CarProfiles>();
        services.AddScoped<BestNearYou>();
        services.AddScoped<SavedPlaces>();
        services.AddScoped<TripState>();
        services.AddScoped<PlaceSearch>();
        services.AddScoped<PlaceAddresses>();
        services.TryAddScoped<BrowserDevice>();
        services.TryAddScoped<IDeviceLocation>(sp => sp.GetRequiredService<BrowserDevice>());
        services.TryAddScoped<IPreferenceStore>(sp => sp.GetRequiredService<BrowserDevice>());
        services.TryAddScoped<ISessionStore>(sp => sp.GetRequiredService<BrowserDevice>().Session);
        if (options.ReleaseNotesUrl is { } notesUrl)
        {
            var notesBaseUrl = notesUrl.AbsoluteUri.EndsWith('/') ? notesUrl : new Uri(notesUrl.AbsoluteUri + "/");
            services.TryAddSingleton<IReleaseNotesSource>(
                _ => new HttpReleaseNotesSource(new HttpClient { BaseAddress = notesBaseUrl, Timeout = TimeSpan.FromSeconds(20) }));
        }

        services.TryAddSingleton<IReleaseNotesSource, NoReleaseNotes>();
        services.AddScoped<ReleaseNotes>();
        services.AddScoped<UpdateCheck>();
        return services;
    }
}
