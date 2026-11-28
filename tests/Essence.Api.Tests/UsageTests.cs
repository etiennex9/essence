using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Api.Tests;

/// <summary>
/// The usage counts (README → Usage): which requests count, for whom, and what <see cref="UsageCounter"/> writes. No
/// address, location or path is kept; a browser is a hash that changes with the day's secret.
/// </summary>
public sealed class UsageTests
{
    private const string Chrome =
        "Mozilla/5.0 (Linux; Android 15; SM-S921W) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Mobile Safari/537.36";

    private static readonly DateOnly Day = new(2026, 11, 22);
    private static readonly Guid Install = Guid.Parse("4b1f6f0e9d5c4a3b8e2f1a0b9c8d7e6f");
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void The_Android_app_counts_by_a_hash_of_its_installation_s_id_with_the_build_its_User_Agent_names()
    {
        var sighting = Usage.Classify(Android("Essence-Android/2026.11.22 (463500)", Install.ToString("N")), Address, Day);

        var android = Assert.IsType<AndroidSighting>(sighting);
        Assert.Equal(new AndroidSighting(Day, Usage.InstallKey(Install), 463500, "2026.11.22"), android);
        Assert.NotEqual(Install, android.Install);
    }

    [Theory]
    [InlineData("Essence-Android/2026.11.18.2 (463007)", null)]
    [InlineData("Essence-Android/2026.11.18.2 (463007)", "not an id")]
    [InlineData("Essence-Android/2026.11.18.2", "4b1f6f0e9d5c4a3b8e2f1a0b9c8d7e6f")]
    [InlineData("Essence-Android/ (463007)", "4b1f6f0e9d5c4a3b8e2f1a0b9c8d7e6f")]
    public void An_Android_build_without_its_installation_s_id_or_its_version_isn_t_counted(string userAgent, string? install)
    {
        Assert.Null(Usage.Classify(Android(userAgent, install), Address, Day));
    }

    [Fact]
    public void A_browser_s_calls_from_the_web_app_count_with_its_address_and_system()
    {
        var sighting = Usage.Classify(Browser(Chrome, "cors"), Address, Day);

        Assert.Equal(new WebSighting(Day, Address, Chrome, "android"), sighting);
    }

    [Fact]
    public void An_IPv4_address_seen_through_IPv6_is_the_same_visitor()
    {
        var sighting = Usage.Classify(Browser(Chrome, "same-origin"), Address.MapToIPv6(), Day);

        Assert.Equal(Address, Assert.IsType<WebSighting>(sighting).Address);
    }

    [Theory]
    [InlineData(Chrome, "navigate")]
    [InlineData(Chrome, null)]
    [InlineData("Uptime-Kuma/2.0.2", null)]
    [InlineData("Wget", null)]
    [InlineData("curl/8.11.1", "cors")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "cors")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/142.0.0.0 Safari/537.36", "cors")]
    public void Monitors_crawlers_scripts_and_opening_an_API_address_in_a_tab_aren_t_counted(string userAgent, string? mode)
    {
        Assert.Null(Usage.Classify(Browser(userAgent, mode), Address, Day));
    }

    [Theory]
    [InlineData(Chrome, "android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 26_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.1 Mobile/15E148 Safari/604.1", "ios")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0", "windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.1 Safari/605.1.15", "macos")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:145.0) Gecko/20100101 Firefox/145.0", "linux")]
    [InlineData("Mozilla/5.0 (X11; CrOS x86_64 16433.47.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36", "chromeos")]
    [InlineData("Mozilla/5.0 (PlayStation; PlayStation 5/2.26) AppleWebKit/605.1.15 (KHTML, like Gecko)", "other")]
    public void The_system_is_the_one_the_User_Agent_names(string userAgent, string os)
    {
        Assert.Equal(os, Usage.OsOf(userAgent));
    }

    [Fact]
    public void A_visitor_is_another_one_with_another_day_s_secret()
    {
        var first = Usage.VisitorKey([1, 2, 3], Address, Chrome);

        Assert.Equal(first, Usage.VisitorKey([1, 2, 3], Address, Chrome));
        Assert.NotEqual(first, Usage.VisitorKey([4, 5, 6], Address, Chrome));
        Assert.NotEqual(first, Usage.VisitorKey([1, 2, 3], IPAddress.Parse("203.0.113.8"), Chrome));
    }

    [Fact]
    public async Task Each_installation_and_visitor_is_written_once_a_day_whatever_its_requests()
    {
        var (counter, store, _) = Counter();
        for (var i = 0; i < 3; i++)
        {
            counter.Observe(AndroidContext("Essence-Android/2026.11.22 (463500)", Install));
            counter.Observe(BrowserContext(Chrome, Address));
        }

        await counter.FlushAsync(TestContext.Current.CancellationToken);
        counter.Observe(AndroidContext("Essence-Android/2026.11.22 (463500)", Install));
        counter.Observe(BrowserContext(Chrome, Address));
        await counter.FlushAsync(TestContext.Current.CancellationToken);

        var android = Assert.Single(store.Android);
        Assert.Equal(new AndroidSighting(Day, Usage.InstallKey(Install), 463500, "2026.11.22"), android);
        var visit = Assert.Single(store.Web);
        Assert.Equal(new WebVisit(Day, Usage.VisitorKey(store.Salts[Day], Address, Chrome), "android"), visit);
    }

    [Fact]
    public async Task An_installation_that_updates_during_the_day_counts_under_both_builds()
    {
        var (counter, store, _) = Counter();
        counter.Observe(AndroidContext("Essence-Android/2026.11.22 (463500)", Install));
        counter.Observe(AndroidContext("Essence-Android/2026.11.22.2 (463650)", Install));

        await counter.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal([463500L, 463650L], store.Android.Select(a => a.VersionCode).Order());
    }

    [Fact]
    public async Task The_next_day_counts_again_and_its_visitors_get_new_hashes()
    {
        var (counter, store, time) = Counter();
        counter.Observe(AndroidContext("Essence-Android/2026.11.22 (463500)", Install));
        counter.Observe(BrowserContext(Chrome, Address));
        await counter.FlushAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromDays(1));
        counter.Observe(AndroidContext("Essence-Android/2026.11.22 (463500)", Install));
        counter.Observe(BrowserContext(Chrome, Address));
        await counter.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Day, Day.AddDays(1)], store.Android.Select(a => a.Day));
        Assert.Equal([Day, Day.AddDays(1)], store.Web.Select(w => w.Day));
        Assert.NotEqual(store.Web[0].Visitor, store.Web[1].Visitor);
    }

    [Fact]
    public async Task What_couldn_t_be_written_is_written_at_the_next_try()
    {
        var (counter, store, _) = Counter();
        store.Fail = true;
        counter.Observe(AndroidContext("Essence-Android/2026.11.22 (463500)", Install));
        counter.Observe(BrowserContext(Chrome, Address));

        await counter.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Empty(store.Android);

        store.Fail = false;
        await counter.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Single(store.Android);
        Assert.Single(store.Web);
    }

    [Fact]
    public async Task A_visit_from_a_day_whose_secret_is_gone_isn_t_counted()
    {
        var (counter, store, time) = Counter();
        counter.Observe(BrowserContext(Chrome, Address));
        // Seen just before midnight, written after it: the store has no secret for yesterday any more.
        time.Advance(TimeSpan.FromDays(1));

        await counter.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.Web);
    }

    [Fact]
    public void Without_the_usage_role_s_connection_string_nothing_is_counted()
    {
        using var without = Factory();
        using var with = Factory(("ConnectionStrings:Usage", "Host=localhost;Database=unused"));

        Assert.Null(without.Services.GetService<UsageCounter>());
        Assert.NotNull(with.Services.GetService<UsageCounter>());
    }

    private static (UsageCounter Counter, FakeUsageStore Store, FakeTimeProvider Time) Counter()
    {
        // Noon in Québec on Day.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 17, 0, 0, TimeSpan.Zero));
        var store = new FakeUsageStore();
        return (new UsageCounter(store, time, NullLogger<UsageCounter>.Instance), store, time);
    }

    private static HttpRequest Android(string userAgent, string? install) =>
        AndroidContext(userAgent, install).Request;

    private static HttpContext AndroidContext(string userAgent, Guid install) => AndroidContext(userAgent, install.ToString("N"));

    private static HttpContext AndroidContext(string userAgent, string? install)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        if (install is not null)
        {
            context.Request.Headers[Usage.InstallHeader] = install;
        }

        context.Connection.RemoteIpAddress = Address;
        return context;
    }

    private static HttpRequest Browser(string userAgent, string? mode) => BrowserContext(userAgent, Address, mode).Request;

    private static HttpContext BrowserContext(string userAgent, IPAddress address, string? mode = "cors")
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        if (mode is not null)
        {
            context.Request.Headers["Sec-Fetch-Mode"] = mode;
        }

        context.Connection.RemoteIpAddress = address;
        return context;
    }

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Essence", "Host=localhost;Database=unused");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private sealed class FakeUsageStore : IUsageStore
    {
        public List<AndroidSighting> Android { get; } = [];

        public List<WebVisit> Web { get; } = [];

        public Dictionary<DateOnly, byte[]> Salts { get; } = [];

        public bool Fail { get; set; }

        public Task<byte[]?> SaltAsync(DateOnly day, DateOnly today, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("The database is down.");
            }

            foreach (var old in Salts.Keys.Where(d => d < today).ToList())
            {
                Salts.Remove(old);
            }

            if (!Salts.TryGetValue(day, out var salt) && day >= today)
            {
                salt = Salts[day] = Guid.NewGuid().ToByteArray();
            }

            return Task.FromResult(salt);
        }

        public Task WriteAsync(IReadOnlyCollection<AndroidSighting> android, IReadOnlyCollection<WebVisit> web, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("The database is down.");
            }

            Android.AddRange(android.Except(Android));
            Web.AddRange(web.Except(Web));
            return Task.CompletedTask;
        }
    }
}
