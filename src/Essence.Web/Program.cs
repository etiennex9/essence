using Essence.UI;
using Essence.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// wwwroot/appsettings.json; in Docker it's written at container start from ESSENCE_API_BASE_URL.
// A relative address (the container's default, /api/) is resolved against the page's. It's tried first because outside
// Windows, an absolute parse reads "/api/" as a file path.
var apiBaseUrl = builder.Configuration["Api:BaseUrl"];
var apiUri = Uri.TryCreate(apiBaseUrl, UriKind.Relative, out var relativeApiUri)
    ? new Uri(new Uri(builder.HostEnvironment.BaseAddress), relativeApiUri)
    : Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var absoluteApiUri) ? absoluteApiUri : null;
if (apiUri is null)
{
    throw new InvalidOperationException($"Api:BaseUrl must be a URL or a path (got \"{apiBaseUrl}\").");
}

#if DEBUG
// Development only, in a Debug build (dotnet watch): ?now=2026-11-11T21:15:00-05:00 on the page's address starts the app's
// clock at that moment, moving on with the real one, to check stations' hours by hand at another time of day. Registered
// before AddEssenceUI, whose TimeProvider.System then stands aside. A published app has none of it.
if (builder.HostEnvironment.IsDevelopment())
{
    builder.Services.AddSingleton<TimeProvider>(services =>
    {
        var address = services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;
        return SimulatedTime.Parse(address) is { } start ? new SimulatedTime(start) : TimeProvider.System;
    });
}
#endif

var defaults = new EssenceUIOptions { ApiBaseUrl = apiUri };
builder.Services.AddEssenceUI(new EssenceUIOptions
{
    ApiBaseUrl = apiUri,
    MapStyleUrl = builder.Configuration["Map:StyleUrl"] ?? defaults.MapStyleUrl,
    MapDarkStyleUrl = builder.Configuration["Map:DarkStyleUrl"] ?? defaults.MapDarkStyleUrl,
    // The image's release notes (see its Dockerfile), next to the page.
    ReleaseNotesUrl = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "release/"),
});

var host = builder.Build();
#if DEBUG
// The clock reads the address it opened at now, before the page changes it.
_ = host.Services.GetRequiredService<TimeProvider>();
#endif
await host.RunAsync();
