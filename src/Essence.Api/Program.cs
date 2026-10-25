using Essence.Api;
using Essence.Api.Endpoints;
using Essence.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Essence");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Essence is not configured.");
}

// Connects as the read-only essence_reader role.
builder.Services.AddEssenceDbContext(connectionString, o => o.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StationSnapshot>();
builder.Services.AddOsrmRouting(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<IngestHealthCheck>("ingest")
    .AddCheck<RoutingHealthCheck>("routing", HealthStatus.Degraded);
builder.Services.AddPerIpRateLimiting(builder.Configuration);

// Essence.Web's nginx forwards /api/ here with the client's address (for the rate limit) and the public scheme, host and
// /api prefix (for the OpenAPI document and Scalar). Trusted only from private networks, where that container lives.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedPrefix;
    foreach (var network in (string[])["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"])
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
{
    if (corsOrigins.Contains("*"))
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        policy.WithOrigins(corsOrigins);
    }

    policy.WithMethods("GET").AllowAnyHeader();
}));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", (HttpRequest request) => TypedResults.Redirect($"{request.PathBase}/scalar")).ExcludeFromDescription();
app.MapHealthChecks("/health", IngestHealthCheck.EndpointOptions);
// Liveness only, whatever the last import's age: the web container's healthcheck asks for it through its /api/ proxy.
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = _ => false });

app.MapStationEndpoints();
app.MapTripEndpoints();
app.MapPriceEndpoints();
app.MapImportEndpoints();

app.Run();
