using Essence.Contracts;

namespace Essence.Api.Endpoints;

/// <summary>
/// <c>/client-config</c>: what the apps need of this deployment's settings. The Android app chooses its server when it
/// runs, so the OpenStreetMap application its users link their own account with comes from here, not from the build.
/// </summary>
public static class ClientConfigEndpoints
{
    public const string SectionName = "Osm";

    public const string DefaultAuthUrl = "https://www.openstreetmap.org";
    public const string DefaultApiUrl = "https://api.openstreetmap.org";

    /// <summary>Maps <c>/client-config</c>, read once here: a wrong address stops the API at its start rather than at a request.</summary>
    public static IEndpointRouteBuilder MapClientConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var config = Read(app.ServiceProvider.GetRequiredService<IConfiguration>());
        app.MapGet("/client-config", () => TypedResults.Ok(config))
            .WithTags("Client")
            .WithSummary("What the apps need of this server's settings")
            .WithDescription(
                "`osmClientId` is the client id of the deployment's OpenStreetMap OAuth 2 application (a public client: no "
                + "secret), with which a device links its own OpenStreetMap account to send its corrections of a station's "
                + "hours there; the token stays on the device, and this server never sees it. Null when `Osm:ClientId` isn't "
                + "set: editing OpenStreetMap is off. `osmAuthUrl` signs in and gives the token (`Osm:AuthUrl`), `osmApiUrl` "
                + "takes the edits (`Osm:ApiUrl`); OpenStreetMap's own by default, or its development server for tests.")
            .RequireRateLimiting(RateLimiting.PolicyName);
        return app;
    }

    /// <summary>
    /// The settings as served: <c>Osm:ClientId</c>, null when it's empty; <c>Osm:AuthUrl</c> and <c>Osm:ApiUrl</c> without a
    /// trailing slash, OpenStreetMap's own when they're empty. The apps refuse servers that <see cref="OsmServers"/> doesn't
    /// take, so the API doesn't start on them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An address isn't an https URL (http only on the machine itself), or the API isn't the sign-in's own: one set to the
    /// development server and the other left to OpenStreetMap's.
    /// </exception>
    public static ClientConfig Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var clientId = section["ClientId"]?.Trim();
        var auth = Url(section, "AuthUrl", DefaultAuthUrl);
        var api = Url(section, "ApiUrl", DefaultApiUrl);
        if (!OsmServers.ApiBelongsTo(auth, api))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ApiUrl must be the API of the OpenStreetMap at {SectionName}:AuthUrl: the same server, or "
                + $"{DefaultApiUrl} for {DefaultAuthUrl} (got \"{api}\" for \"{auth}\").");
        }

        return new ClientConfig(
            string.IsNullOrEmpty(clientId) ? null : clientId,
            auth.AbsoluteUri.TrimEnd('/'),
            api.AbsoluteUri.TrimEnd('/'));
    }

    private static Uri Url(IConfigurationSection section, string key, string fallback)
    {
        var text = section[key]?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new Uri(fallback);
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var url) && OsmServers.IsSafe(url)
            ? url
            : throw new InvalidOperationException(
                $"{SectionName}:{key} must be an https address, or http on this machine (got \"{text}\").");
    }
}
