using Essence.UI;
using Essence.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// wwwroot/appsettings.json; in Docker it's written at container start from ESSENCE_API_BASE_URL.
var apiBaseUrl = builder.Configuration["Api:BaseUrl"];
if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri))
{
    throw new InvalidOperationException($"Api:BaseUrl must be an absolute URL (got \"{apiBaseUrl}\").");
}

var defaults = new EssenceUIOptions { ApiBaseUrl = apiUri };
builder.Services.AddEssenceUI(new EssenceUIOptions
{
    ApiBaseUrl = apiUri,
    MapStyleUrl = builder.Configuration["Map:StyleUrl"] ?? defaults.MapStyleUrl,
    MapDarkStyleUrl = builder.Configuration["Map:DarkStyleUrl"] ?? defaults.MapDarkStyleUrl,
});

await builder.Build().RunAsync();
