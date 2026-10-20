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

var defaults = new EssenceUIOptions { ApiBaseUrl = apiUri };
builder.Services.AddEssenceUI(new EssenceUIOptions
{
    ApiBaseUrl = apiUri,
    MapStyleUrl = builder.Configuration["Map:StyleUrl"] ?? defaults.MapStyleUrl,
    MapDarkStyleUrl = builder.Configuration["Map:DarkStyleUrl"] ?? defaults.MapDarkStyleUrl,
    // The image's release notes (see its Dockerfile), next to the page.
    ReleaseNotesUrl = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "release/"),
});

await builder.Build().RunAsync();
