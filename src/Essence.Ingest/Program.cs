using System.Net;
using Essence.Data;
using Essence.Ingest;
using Essence.Ingest.Backfill;
using Essence.Ingest.Feed;
using Essence.Ingest.Scheduling;
using Essence.Ingest.Storage;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Essence");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Essence is not configured.");
}

builder.Services.AddEssenceDbContext(connectionString);
builder.Services.AddSingleton(TimeProvider.System);

var feedSection = builder.Configuration.GetSection(FeedOptions.SectionName);
var feed = feedSection.Get<FeedOptions>() ?? new FeedOptions();
builder.Services.AddOptions<FeedOptions>()
    .Bind(feedSection)
    .Validate(o => o.PublishInterval > TimeSpan.Zero && o.FastPollInterval > TimeSpan.Zero && o.SlowPollInterval > TimeSpan.Zero,
        "Feed intervals must be positive.")
    .Validate(o => o.MinDelay <= o.MaxDelay, "Feed:MinDelay must not exceed Feed:MaxDelay.")
    .Validate(o => o.DelayPercentile is >= 1 and <= 100 && o.DelayHistorySize > 0, "Feed:DelayPercentile must be 1–100 and Feed:DelayHistorySize positive.")
    .ValidateOnStart();
builder.Services.AddOptions<BackfillOptions>()
    .Bind(builder.Configuration.GetSection(BackfillOptions.SectionName))
    .Validate(o => o.BaseUrl is { IsAbsoluteUri: true } url && url.AbsolutePath.EndsWith('/') && o.MaxAge > TimeSpan.Zero,
        "Backfill:BaseUrl must be an absolute URL ending with '/' and Backfill:MaxAge must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<ArchiveOptions>()
    .Bind(builder.Configuration.GetSection(ArchiveOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Path) && o.RetentionDays > 0, "Archive:Path is required and Archive:RetentionDays must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton<PollScheduler>();
builder.Services.AddSingleton<RawArchive>();
builder.Services.AddScoped<ImportWriter>();

builder.Services.AddHttpClient<FeedClient>(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan; // The resilience pipeline owns timeouts.
        client.MaxResponseContentBufferSize = 32 * 1024 * 1024;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Essence-Ingest/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Keep the raw gzip bytes for the archive; FeedClient decompresses itself.
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = feed.RequestTimeout;
        o.Retry.MaxRetryAttempts = feed.RetryCount;
        o.TotalRequestTimeout.Timeout = feed.RequestTimeout * (feed.RetryCount + 1) + TimeSpan.FromSeconds(15);
        o.CircuitBreaker.SamplingDuration = feed.RequestTimeout * 2;
    });

builder.Services.AddHostedService<IngestWorker>();

builder.Build().Run();
