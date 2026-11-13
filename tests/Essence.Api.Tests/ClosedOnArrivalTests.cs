using System.Net;
using System.Text.Json;
using Essence.Contracts;
using Essence.Hours;
using Essence.Ranking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Api.Tests;

/// <summary>
/// What /stations/best and /trips/best add for stations closed on arrival: the <c>ignoreHours</c> parameter, the stations
/// said to be left out, the 304 rule of an answer that depends on the clock, and the Development clock. The endpoints read
/// the database, which these tests don't have: the rules are checked on values given here, and the parameters in the API's
/// description.
/// </summary>
public sealed class ClosedOnArrivalTests
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";

    [Theory]
    [InlineData(null, new long[0])]
    [InlineData("", new long[0])]
    [InlineData("  ", new long[0])]
    [InlineData("42", new long[] { 42 })]
    [InlineData("42,7, 42 ,1300", new long[] { 42, 7, 1300 })]
    public void Ignored_hours_are_station_ids_separated_by_commas(string? text, long[] ids) =>
        Assert.Equal(ids.ToHashSet(), IgnoredHours.Parse(text));

    [Theory]
    [InlineData("1,,2")]
    [InlineData("1,")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("+3")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public void Anything_else_is_refused(string text) => Assert.Null(IgnoredHours.Parse(text));

    [Fact]
    public void At_most_50_ids_are_taken()
    {
        Assert.Equal(50, IgnoredHours.Parse(string.Join(",", Enumerable.Range(1, 50)))!.Count);
        Assert.Null(IgnoredHours.Parse(string.Join(",", Enumerable.Range(1, 51))));
    }

    [Fact]
    public void Only_stations_cheaper_than_the_pick_are_said_to_be_left_out_at_most_two_lowest_cost_first()
    {
        var hours = new Dictionary<long, SnapshotHours>();
        LeftOutCandidate Candidate(long id, decimal price, decimal cost)
        {
            hours[id] = Hours(CostcoRule);
            return new LeftOutCandidate(Station(id), price, cost, EtaMinutes: 12.34);
        }

        LeftOutCandidate[] leftOut =
        [
            Candidate(1, 155.9m, 63.20m),
            Candidate(2, 164.9m, 66.10m),
            Candidate(3, 154.9m, 64.00m),
            Candidate(4, 153.9m, 62.80m),
            Candidate(5, 160.0m, 64.50m),
        ];

        var said = LeftOutStations.Of(leftOut, hours, pickPriceCents: 160.0m)!;

        Assert.Equal([4L, 1L], said.Select(s => s.Id));
        var costco = said[0];
        Assert.Equal(
            new LeftOutStation(4, "Costco Laval", "Costco", "2999 Autoroute Chomedey", 45.5699, -73.7469, 153.9m, hours[4].Dto, 12.3),
            costco);
    }

    [Fact]
    public void Nothing_cheaper_or_no_pick_says_nothing()
    {
        var hours = new Dictionary<long, SnapshotHours> { [1] = Hours(CostcoRule) };
        LeftOutCandidate[] leftOut = [new(Station(1), 165.9m, 66m, 5)];

        Assert.Null(LeftOutStations.Of(leftOut, hours, pickPriceCents: 165.9m));
        Assert.Null(LeftOutStations.Of(leftOut, hours, pickPriceCents: null));
        Assert.Null(LeftOutStations.Of([], hours, pickPriceCents: 170m));
        Assert.Single(LeftOutStations.Of(leftOut, hours, pickPriceCents: 166m)!);
    }

    [Fact]
    public void A_station_s_hours_are_read_for_its_arrival_as_served()
    {
        var hours = Hours(CostcoRule) with { Dto = Hours(CostcoRule).Dto with { Trusted = false, TemporarilyClosed = true } };

        Assert.Equal(new ArrivalHours(hours.Hours, QuebecTime.Eastern, Trusted: false, TemporarilyClosed: true), hours.Arrival);
    }

    [Fact]
    public void An_answer_that_depends_on_the_clock_gets_no_304_once_its_data_is_10_minutes_old()
    {
        var feed = new DateTimeOffset(2026, 11, 12, 21, 5, 1, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(10), DataVersion.ClockDependentFor);
        Assert.True(DataVersion.IsNotModified(feed, feed, null, clockDependentAt: feed.AddMinutes(4)));
        Assert.True(DataVersion.IsNotModified(feed, feed, null, clockDependentAt: feed.AddMinutes(10)));
        Assert.False(DataVersion.IsNotModified(feed, feed, null, clockDependentAt: feed.AddMinutes(10).AddSeconds(1)));

        // An answer that doesn't still gets one, and the other rules still hold.
        Assert.True(DataVersion.IsNotModified(feed, feed, null, clockDependentAt: null));
        Assert.True(DataVersion.IsNotModified(feed, feed, null));
        Assert.False(DataVersion.IsNotModified(feed, feed, feed.AddSeconds(1), clockDependentAt: feed.AddMinutes(1)));
        Assert.False(DataVersion.IsNotModified(feed.AddSeconds(-1), feed, null, clockDependentAt: feed.AddMinutes(1)));
    }

    [Fact]
    public void In_Development_Clock_Now_starts_the_clock_at_that_moment_and_it_moves_on()
    {
        var real = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.Zero));

        var clock = SimulatedTime.For(isDevelopment: true, "2026-11-11T21:15:00-05:00", real);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 21, 15, 0, TimeSpan.FromHours(-5)), clock.GetUtcNow());
        real.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 21, 35, 0, TimeSpan.FromHours(-5)), clock.GetUtcNow());
    }

    [Fact]
    public void Clock_Now_is_ignored_outside_Development_and_without_a_value()
    {
        var real = new FakeTimeProvider();

        Assert.Same(real, SimulatedTime.For(isDevelopment: false, "2026-11-11T21:15:00-05:00", real));
        Assert.Same(real, SimulatedTime.For(isDevelopment: true, null, real));
        Assert.Same(real, SimulatedTime.For(isDevelopment: true, " ", real));
        Assert.Same(TimeProvider.System, SimulatedTime.For(isDevelopment: false, null));
    }

    [Theory]
    [InlineData("2026-11-11T21:15:00")]
    [InlineData("2026-11-11")]
    [InlineData("tomorrow")]
    public void In_Development_a_Clock_Now_without_its_offset_stops_the_start(string now) =>
        Assert.Throws<InvalidOperationException>(() => SimulatedTime.For(isDevelopment: true, now));

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void The_API_s_clock_follows_Clock_Now_only_in_Development(string environment, bool simulated)
    {
        using var factory = Factory(environment, "2026-11-11T21:15:00-05:00");
        _ = factory.CreateClient();

        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();

        var simulatedStart = new DateTimeOffset(2026, 11, 11, 21, 15, 0, TimeSpan.FromHours(-5));
        Assert.Equal(simulated, now - simulatedStart is var since && since >= TimeSpan.Zero && since < TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("/stations/best")]
    [InlineData("/trips/best")]
    public async Task Best_near_you_and_on_the_way_take_the_ids_whose_hours_to_ignore(string path)
    {
        using var factory = Factory("Production", null);

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var get = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");
        var ignoreHours = get.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "ignoreHours");
        Assert.False(ignoreHours.TryGetProperty("required", out var required) && required.GetBoolean());
        Assert.Contains("leftOut", get.GetProperty("description").GetString());
    }

    private static WebApplicationFactory<Program> Factory(string environment, string? now) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Essence", "Host=localhost;Database=unused");
            if (now is not null)
            {
                builder.UseSetting(SimulatedTime.ConfigKey, now);
            }

            builder.ConfigureTestServices(services =>
            {
                foreach (var service in (DataService[])[DataService.Routing, DataService.Geocoding])
                {
                    services.AddHttpClient(DataServiceMonitor.ClientName(service))
                        .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
                }
            });
        });

    private static SnapshotStation Station(long id) =>
        new(id, "Costco Laval", "Costco", "Active", "2999 Autoroute Chomedey", 45.5699, -73.7469, MembersOnly: true, []);

    private static SnapshotHours Hours(string rule) =>
        new(new StationHours(rule, "costco", Trusted: true, Confirmed: true, null, null), OpeningHours.Parse(rule), QuebecTime.Eastern);

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
