using Essence.Api;
using Essence.Api.Endpoints;
using Essence.Data;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<IngestHealthCheck>("ingest");
builder.Services.AddPerIpRateLimiting(builder.Configuration);

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", () => TypedResults.Redirect("/scalar")).ExcludeFromDescription();
app.MapHealthChecks("/health", IngestHealthCheck.EndpointOptions);

app.MapStationEndpoints();
app.MapPriceEndpoints();
app.MapImportEndpoints();

app.Run();
