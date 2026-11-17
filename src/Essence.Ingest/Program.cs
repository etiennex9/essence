using System.Net;
using Essence.Data;
using Essence.Ingest;
using Essence.Ingest.Backfill;
using Essence.Ingest.Feed;
using Essence.Ingest.Hours;
using Essence.Ingest.Scheduling;
using Essence.Ingest.Storage;
using Microsoft.Extensions.Options;

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

var hoursSection = builder.Configuration.GetSection(HoursOptions.SectionName);
var hours = hoursSection.Get<HoursOptions>() ?? new HoursOptions();
builder.Services.AddOptions<HoursOptions>()
    .Bind(hoursSection)
    .Validate(o => o.Validate() is null, hours.Validate() ?? "The Hours settings are invalid.")
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

builder.Services.AddSingleton(provider => new HoursSchedule(
    provider.GetRequiredService<IOptions<HoursOptions>>(), provider.GetRequiredService<TimeProvider>(), Random.Shared));
builder.Services.AddSingleton<HoursCache>();
builder.Services.AddSingleton(provider => new HoursPacer(
    provider.GetRequiredService<IOptions<HoursOptions>>(), provider.GetRequiredService<TimeProvider>(), Random.Shared));
builder.Services.AddScoped<HoursWriter>();
builder.Services.AddScoped<IHoursStations>(provider => provider.GetRequiredService<HoursWriter>());
// In the order of their first runs after a start, a minute apart (HoursSchedule.Stagger): Shell's, the longest, last.
builder.Services.AddTransient<IHoursSource, CuratedHoursSource>();
builder.Services.AddTransient<IHoursSource, CostcoSource>();
builder.Services.AddTransient<IHoursSource, UltramarSource>();
builder.Services.AddTransient<IHoursSource, CircleKSource>();
builder.Services.AddTransient<IHoursSource, OverpassSource>();
builder.Services.AddTransient<IHoursSource, ShellSource>();

AddHoursClient(builder.Services.AddHttpClient<HoursClient>(), hours.RequestTimeout);
// Overpass takes a minute or more for all Québec: its own timeout, longer than the query's.
AddHoursClient(builder.Services.AddHttpClient<OverpassClient>(), hours.Osm.RequestTimeout);

builder.Services.AddHostedService<IngestWorker>();
builder.Services.AddHostedService<HoursWorker>();

builder.Build().Run();

// The hours sources' clients: their own User-Agent, compressed answers decompressed, and the pipeline's timeouts, without
// the feed's retries unless asked.
void AddHoursClient(IHttpClientBuilder client, TimeSpan requestTimeout)
{
    client
        .ConfigureHttpClient(http =>
        {
            http.Timeout = Timeout.InfiniteTimeSpan; // The resilience pipeline owns timeouts.
            http.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(hours.UserAgent);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        .AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = requestTimeout;
            // The pipeline wants at least one retry: with none asked, nothing is handled as worth retrying. A source's run
            // then makes only the requests it needs, and a failed run is tried again later (Hours:Backoff).
            o.Retry.MaxRetryAttempts = Math.Max(1, hours.RetryCount);
            if (hours.RetryCount == 0)
            {
                o.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            }

            o.TotalRequestTimeout.Timeout = requestTimeout * (hours.RetryCount + 1) + TimeSpan.FromSeconds(15);
            o.CircuitBreaker.SamplingDuration = requestTimeout * 2;
        });
}
