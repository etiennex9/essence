using System.Net;
using System.Text.Json;
using Essence.Hours;
using Essence.Ranking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Api.Tests;

public class UsuallyCheapTests
{
    private const short Regular = 1;

    /// <summary>Eight days of imports every 5 minutes, to 10 p.m. in Montréal on 2026-11-26.</summary>
    private static readonly DateTimeOffset LastImport = new(2026, 11, 26, 22, 0, 0, TimeSpan.FromHours(-5));

    private readonly FakeTimeProvider _time = new(LastImport.AddMinutes(1));

    [Fact]
    public async Task The_usually_cheap_stations_are_documented()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Essence", "Host=127.0.0.1;Port=1;Database=unused;Timeout=1"));

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var get = document.RootElement.GetProperty("paths").GetProperty("/stations/usually-cheap").GetProperty("get");
        Assert.Equal("Stations", get.GetProperty("tags")[0].GetString());
        Assert.Contains("cheapest fifth", get.GetProperty("description").GetString());
        Assert.Equal(["fuel"], get.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.False(get.GetProperty("responses").TryGetProperty("304", out _));
    }

    [Fact]
    public async Task Requests_while_it_s_computed_share_it_and_it_s_kept_for_the_day()
    {
        var cache = new UsuallyCheapCache(_time, NullLogger<UsuallyCheapCache>.Instance);
        var (snapshot, window) = Data();

        var answers = await Task.WhenAll(
            cache.GetAsync(Regular, snapshot, window, TestContext.Current.CancellationToken),
            cache.GetAsync(Regular, snapshot, window, TestContext.Current.CancellationToken));
        _time.Advance(TimeSpan.FromMinutes(50));
        var later = await cache.GetAsync(Regular, snapshot, window, TestContext.Current.CancellationToken);

        Assert.Equal([1L], answers[0]!.Stations.Ids);
        Assert.Same(answers[0], answers[1]);
        Assert.Same(answers[0], later);
    }

    [Fact]
    public async Task A_new_day_in_Quebec_serves_the_last_answer_while_the_next_is_computed()
    {
        var cache = new UsuallyCheapCache(_time, NullLogger<UsuallyCheapCache>.Instance);
        var (snapshot, window) = Data();
        var first = await cache.GetAsync(Regular, snapshot, window, TestContext.Current.CancellationToken);

        // Past midnight in Montréal: a new day, though not 24 hours later.
        _time.Advance(TimeSpan.FromHours(2));
        var stale = await cache.GetAsync(Regular, snapshot, window, TestContext.Current.CancellationToken);

        Assert.Same(first, stale);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        UsualCheapness? next;
        do
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            next = await cache.GetAsync(Regular, snapshot, window, TestContext.Current.CancellationToken);
        }
        while (ReferenceEquals(next, first) && DateTime.UtcNow < deadline);

        Assert.NotSame(first, next);
        Assert.Equal([1L], next!.Stations.Ids);
    }

    [Fact]
    public async Task Nothing_before_the_first_import()
    {
        var cache = new UsuallyCheapCache(_time, NullLogger<UsuallyCheapCache>.Instance);

        Assert.Null(await cache.GetAsync(Regular, Data().Snapshot, PriceWindow.Empty, TestContext.Current.CancellationToken));
    }

    /// <summary>Six stations about 300 m apart, the first the cheapest all along, and one closed.</summary>
    private static (Snapshot Snapshot, PriceWindow Window) Data()
    {
        var from = LastImport.AddDays(-8);
        var imports = new List<ImportTime>();
        for (var at = from; at <= LastImport; at = at.AddMinutes(5))
        {
            imports.Add(new ImportTime(imports.Count + 1, at));
        }

        decimal[] prices = [150m, 160m, 160m, 161m, 162m, 165m, 140m];
        var stations = prices
            .Select((cents, i) => new SnapshotStation(
                i + 1, null, "Ultramar", i == 6 ? "Fermé temporairement" : Eligibility.Operating, $"{i + 1} rue Principale",
                45.5, -73.6 + i * 0.3 / 78, false, [new SnapshotPrice(Regular, cents, true)]))
            .ToList();
        var rows = prices.Select((cents, i) => new HistoryRow(i + 1, i + 1, Regular, cents, true, from, null));
        var window = PriceWindow.Empty.With(imports, rows, [], from.AddDays(-1));
        var snapshot = new Snapshot(LastImport, stations, null, new Dictionary<long, SnapshotHours>(), null, null);
        return (snapshot, window);
    }
}
