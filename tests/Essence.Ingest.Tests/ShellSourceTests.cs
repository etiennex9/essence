using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Essence.Data.Entities;
using Essence.Hours;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Time.Testing;
using static Essence.Ingest.Tests.HoursFixtures;

namespace Essence.Ingest.Tests;

/// <summary>
/// Shell's locator against trimmed real answers (Fixtures/Hours/shell-list.json, a list of stations in Laval, Québec City
/// and Montmagny, and shell-detail.json, their details, fetched 2026-11-12).
/// </summary>
public sealed class ShellSourceTests
{
    private const string ListPath = "/api/v2/locations/within_bounds";

    /// <summary>The feed's Shell stations by three of the listed ones, and an Esso by a fourth.</summary>
    private static readonly MatchStation[] FeedStations =
    [
        Station(1, "4055 boul. Industriel, Laval", "H7L 4S3", "Shell", 45.5984, -73.7503),
        Station(2, "2131 boul. des Laurentides, Laval", "H7M 4M2", "Shell", 45.6089, -73.7341),
        Station(3, "55 montée de la Rivière-du-Sud, Montmagny", "G5V 2P5", "Shell", 46.9714, -70.5562),
        // Within reach of 10052881, but an Esso: it's never asked about.
        Station(4, "1625 boul. Saint-Martin O., Laval", "H7S 1N1", "Esso", 45.5685, -73.7273),
        // By 10053079, which the list says is inactive.
        Station(5, "7900 ch. de Chambly, Longueuil", "J3Y 5K2", "Shell", 45.4876, -73.3827),
        // 1 km from 10052586.
        Station(6, "2200 ch. Sainte-Foy, Québec", "G1V 1S1", "Shell", 46.798, -71.270),
    ];

    private static ShellSource Source(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, HoursPacer? pacer = null) =>
        new(Client(respond), pacer ?? NoPause(), new Stations(FeedStations), Options());

    private static HoursParse Parse(JsonArray details) => Source().Parse(Utf8(new JsonObject { ["locations"] = details }));

    private static JsonArray Details() => Json("shell-detail.json").AsArray();

    private static JsonArray Detail(string forecourt, string? forecourtStatus = null, string opening = "[]")
    {
        var detail = Details().OfType<JsonObject>().First().DeepClone().AsObject();
        detail["forecourt_opening_hours"] = JsonNode.Parse(forecourt);
        detail["opening_hours"] = JsonNode.Parse(opening);
        detail["forecourt_open_status"] = forecourtStatus;
        detail["open_status"] = null;
        return [detail];
    }

    /// <summary>The locator: Québec's box answers one cluster, the cluster the list, and each detail as in the fixture.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Locator(List<string> requests, Func<string, HttpResponseMessage?>? detail = null) =>
        request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.PathAndQuery);
            if (uri.AbsolutePath == ListPath)
            {
                return requests.Count(r => r.StartsWith(ListPath, StringComparison.Ordinal)) == 1
                    ? Ok("""{"locations":[],"clusters":[{"centroid":[45.6,-73.7],"bounds":{"sw":[45.48,-73.76],"ne":[46.98,-70.55]},"size":7,"id":"a"}]}""")
                    : Ok(Bytes("shell-list.json"));
            }

            var id = uri.AbsolutePath.Split('/')[^1];
            return detail?.Invoke(id)
                ?? (Details().OfType<JsonObject>().FirstOrDefault(d => (string?)d["id"] == id) is { } found
                    ? Ok(found.ToJsonString())
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        };

    /// <summary>A detail failing: an error status, no answer in time, or a block page.</summary>
    private static HttpResponseMessage Failure(string failure) => failure switch
    {
        "502" => new HttpResponseMessage(HttpStatusCode.BadGateway),
        "not JSON" => Ok("<html><body>Sorry, you have been blocked</body></html>"),
        _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
    };

    [Fact]
    public void Each_station_s_forecourt_week_is_read_its_days_being_ranges()
    {
        var parsed = Parse(Details());

        Assert.Equal(
            [
                ("12200873", "Mo-Fr 04:45-21:30; Sa-Su 07:00-21:00"),
                ("12280964", "24/7"),
                ("10052881", "Mo-Fr 07:00-19:30; Sa 08:00-18:00; Su 09:00-18:00"),
                ("10052586", "Mo-Th 07:00-21:00; Fr 05:00-21:00; Sa-Su 08:00-21:00"),
                ("13231467", "Mo-Su 07:00-21:59"),
            ],
            parsed.Records.Select(r => (r.Ref, r.Rule!)));
        Assert.Empty(parsed.Warnings);
        Assert.Equal(
            new SourceRecord("12200873", "shell", 45.59807, -73.750277, "4055", "H7L4S3", "Mo-Fr 04:45-21:30; Sa-Su 07:00-21:00",
                "https://find.shell.com/ca/fuel/12200873-4055-boul-industriel-laval", null, false),
            parsed.Records[0]);
    }

    [Theory]
    [InlineData("""[{"days":["Mon","Sun"],"hours":[["06:00","22:00"]]}]""", "Mo-Su 06:00-22:00")]
    [InlineData("""[{"days":["Wed","Tue"],"hours":[["06:00","22:00"]]}]""", "Mo-Su 06:00-22:00")]
    [InlineData("""[{"days":["Mon","Fri"],"hours":[["06:00","22:00"]]},{"days":["Sat","Sun"],"hours":[["08:00","20:00"]]}]""", "Mo-Fr 06:00-22:00; Sa-Su 08:00-20:00")]
    [InlineData("""[{"days":["Sat","Mon"],"hours":[["08:00","20:00"]]},{"days":["Tue","Fri"],"hours":[["18:00","02:00"]]}]""", "Sa-Mo 08:00-20:00; Tu-Fr 18:00-02:00")]
    [InlineData("""[{"days":["Mon","Sun"],"hours":[["06:00","12:00"],["13:00","22:00"]]}]""", "Mo-Su 06:00-12:00,13:00-22:00")]
    public void Day_ranges_wrap_across_the_weekend_and_hours_can_spill_past_midnight(string forecourt, string expected)
    {
        var record = Assert.Single(Parse(Detail(forecourt)).Records);

        Assert.Equal(OpeningHours.Parse(expected), OpeningHours.Parse(record.Rule!));
    }

    [Fact]
    public void A_station_open_24_hours_is_24_7_with_its_days_or_without_any()
    {
        Assert.Equal("24/7", Assert.Single(Parse(Detail("[]", "twenty_four_hour")).Records).Rule);
        Assert.Equal("24/7", Assert.Single(Parse(Detail("""[{"days":["Mon","Sun"],"hours":[["00:00","23:59"]]}]""", "twenty_four_hour")).Records).Rule);
    }

    [Fact]
    public void The_forecourt_s_hours_come_before_the_station_s()
    {
        var forecourt = Parse(Detail("""[{"days":["Mon","Sun"],"hours":[["06:00","22:00"]]}]""", opening: """[{"days":["Mon","Sun"],"hours":[["05:00","23:00"]]}]"""));
        var station = Parse(Detail("[]", opening: """[{"days":["Mon","Sun"],"hours":[["05:00","23:00"]]}]"""));

        Assert.Equal("Mo-Su 06:00-22:00", Assert.Single(forecourt.Records).Rule);
        Assert.Equal("Mo-Su 05:00-23:00", Assert.Single(station.Records).Rule);
    }

    [Theory]
    [InlineData("""[{"days":["Mon","Sun"],"hours":[["06:00","22:00"]]}]""", "twenty_four_hour", "it says it's open 24 hours, but its days say Mo-Su 06:00-22:00")]
    [InlineData("""[{"days":["Mon","Sat"],"hours":[["06:00","22:00"]]}]""", null, "Sunday's hours aren't given")]
    [InlineData("""[{"days":["Mon","Sun"],"hours":[]}]""", null, "the hours [] aren't understood")]
    [InlineData("""[{"days":["Mon","Wed","Fri"],"hours":[["06:00","22:00"]]}]""", null, "the days [\"Mon\",\"Wed\",\"Fri\"] aren't understood")]
    [InlineData("""[{"days":["Mon","Sun"],"hours":[["06:00","22:00"]]},{"days":["Sun"],"hours":[["08:00","20:00"]]}]""", null, "Sunday's hours are given twice")]
    [InlineData("[]", null, "it gives none")]
    public void Hours_that_can_t_be_read_for_sure_leave_the_station_out_with_a_warning(string forecourt, string? status, string error)
    {
        var parsed = Parse(Detail(forecourt, status));

        Assert.Empty(parsed.Records);
        Assert.Contains($"({error}); left out.", Assert.Single(parsed.Warnings));
    }

    [Fact]
    public async Task Only_stations_within_reach_of_a_feed_Shell_station_are_asked_about_one_every_2_to_3_seconds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 7, 0, 0, TimeSpan.Zero));
        var requests = new List<string>();
        var times = new List<DateTimeOffset>();
        var locator = Locator(requests);
        var source = Source(
            request =>
            {
                times.Add(clock.GetUtcNow());
                return locator(request);
            },
            new HoursPacer(Options(), clock, new Random(5)));

        var fetching = source.FetchAsync(null, TestContext.Current.CancellationToken);
        while (!fetching.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        var fetched = Assert.IsType<HoursFetch.Fetched>(await fetching);

        Assert.Equal(
            [
                ListPath + "?sw%5B%5D=44.9&sw%5B%5D=-79.8&ne%5B%5D=62.6&ne%5B%5D=-57.0",
                // The cluster's bounds, with a margin.
                ListPath + "?sw%5B%5D=45.4795&sw%5B%5D=-73.7605&ne%5B%5D=46.9805&ne%5B%5D=-70.5495",
                "/api/v2/locations/12200873",
                "/api/v2/locations/12280964",
                "/api/v2/locations/13231467",
            ],
            requests);
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(2), $"{pair.First:O} then {pair.Second:O}"));
        Assert.Equal(["12200873", "12280964", "13231467"], source.Parse(fetched.Raw).Records.Select(r => r.Ref));
        Assert.DoesNotContain("telephone", Encoding.UTF8.GetString(fetched.Raw));
        Assert.DoesNotContain("closed", Encoding.UTF8.GetString(fetched.Raw));
    }

    [Theory]
    [InlineData("2125 ch. Sainte-Foy, Québec", "G1V 1R9", 46.798, true)] // 1 km from 10052586, at its address.
    [InlineData("2125 ch. Sainte-Foy, Québec", "G1V 1R9", 46.808, false)] // 2.1 km.
    [InlineData("2125 ch. Sainte-Foy, Québec", "G1V 1S1", 46.798, false)] // The number only.
    [InlineData("2200 ch. Sainte-Foy, Québec", "G1V 1R9", 46.798, false)] // The postal code only.
    public async Task A_feed_Shell_station_with_none_within_reach_has_the_one_at_its_address_within_2_km_asked_about_and_matched(
        string address, string postalCode, double latitude, bool asked)
    {
        var requests = new List<string>();
        var station = Station(6, address, postalCode, "Shell", latitude, -71.270);
        var source = new ShellSource(Client(Locator(requests)), NoPause(), new Stations([.. FeedStations[..5], station]), Options());

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(asked, requests.Contains("/api/v2/locations/10052586"));
        var match = HoursMatcher.Match(source.Parse(fetched.Raw).Records, [station], source.MatchRadiusM).Matches.SingleOrDefault();
        if (asked)
        {
            Assert.Equal(("10052586", StationHoursEntry.ByAddress), (match?.Record.Ref, match?.MatchedBy));
        }
        else
        {
            Assert.Null(match);
        }
    }

    [Fact]
    public async Task A_station_gone_from_the_locator_is_left_out_with_a_warning_and_a_failed_detail_is_asked_again_once()
    {
        var requests = new List<string>();
        var failures = 0;
        var source = Source(Locator(requests, id => id switch
        {
            "13231467" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "12280964" when failures++ == 0 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => null,
        }));

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));
        var parsed = source.Parse(fetched.Raw);

        Assert.Equal(2, requests.Count(r => r.EndsWith("/12280964", StringComparison.Ordinal)));
        Assert.Equal(["12200873", "12280964"], parsed.Records.Select(r => r.Ref));
        Assert.Contains("Shell 13231467: the locator listed it, but no longer had its details (404)", Assert.Single(parsed.Warnings));
    }

    [Theory]
    [InlineData("502")]
    [InlineData("timeout")]
    [InlineData("not JSON")]
    public async Task A_detail_that_fails_twice_leaves_its_station_out_with_a_warning_and_the_run_goes_on(string failure)
    {
        var requests = new List<string>();
        var source = Source(Locator(requests, id => id == "12280964" ? Failure(failure) : null));

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));
        var parsed = source.Parse(fetched.Raw);

        Assert.Equal(2, requests.Count(r => r.EndsWith("/12280964", StringComparison.Ordinal)));
        Assert.Equal(["12200873", "13231467"], parsed.Records.Select(r => r.Ref));
        Assert.StartsWith("Shell 12280964: the locator listed it, but its details failed twice (", Assert.Single(parsed.Warnings));
    }

    [Fact]
    public async Task Details_failing_three_in_a_row_fail_the_run_without_asking_on_but_apart_they_don_t()
    {
        // Every listed Shell station within reach of the feed's, asked about in this order.
        string[] ids = ["10052586", "10052881", "12200873", "12280964", "13231467"];
        var stations = new Stations(
        [
            .. FeedStations,
            Station(7, "1625 boul. Saint-Martin O., Laval", "H7S 1N1", "Shell", 45.5685, -73.7273),
            Station(8, "2125 ch. Sainte-Foy, Québec", "G1V 1R9", "Shell", 46.7891, -71.2703),
        ]);
        ShellSource Failing(List<string> requests, params string[] failing) =>
            new(Client(Locator(requests, id => failing.Contains(id) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null)),
                NoPause(), stations, Options());
        List<string> Asked(List<string> requests) => [.. requests.Where(r => !r.StartsWith(ListPath, StringComparison.Ordinal)).Select(r => r.Split('/')[^1])];

        var inARow = new List<string>();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Failing(inARow, ids[..3]).FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal([.. ids[..3].SelectMany(id => new[] { id, id })], Asked(inARow));
        Assert.Contains("failed the details of 3 stations in a row (10052586, 10052881, 12200873), each asked twice", error.Message);

        var apart = new List<string>();
        var fetched = Assert.IsType<HoursFetch.Fetched>(
            await Failing(apart, "10052586", "10052881", "12280964", "13231467").FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(9, Asked(apart).Count);
        Assert.Equal(["12200873"], Source().Parse(fetched.Raw).Records.Select(r => r.Ref));
        Assert.Equal(4, Source().Parse(fetched.Raw).Warnings.Count);
    }

    [Fact]
    public async Task A_cluster_that_answers_itself_back_is_split_in_four()
    {
        const string Cluster = """{"locations":[],"clusters":[{"bounds":{"sw":[45.48,-73.76],"ne":[46.98,-70.55]},"size":7}]}""";
        var lists = 0;
        var source = Source(request =>
        {
            if (request.RequestUri!.AbsolutePath != ListPath)
            {
                return Ok(Details().OfType<JsonObject>().Single(d => request.RequestUri.AbsolutePath.EndsWith((string)d["id"]!, StringComparison.Ordinal)).ToJsonString());
            }

            lists++;
            return lists <= 2 ? Ok(Cluster) : lists == 3 ? Ok(Bytes("shell-list.json")) : Ok("""{"locations":[],"clusters":[]}""");
        });

        await source.FetchAsync(null, TestContext.Current.CancellationToken);

        // Québec, the cluster, which answered itself, then its four quarters.
        Assert.Equal(6, lists);
    }

    [Fact]
    public async Task A_locator_that_never_stops_answering_clusters_fails_the_run()
    {
        var source = Source(_ => Ok("""{"locations":[],"clusters":[{"bounds":{"sw":[45.0,-74.0],"ne":[46.0,-73.0]},"size":3},{"bounds":{"sw":[46.0,-74.0],"ne":[47.0,-73.0]},"size":3}]}"""));

        await Assert.ThrowsAsync<HoursFormatException>(() => source.FetchAsync(null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("<html></html>")]
    [InlineData("[]")]
    [InlineData("{\"clusters\":[]}")]
    public void Details_kept_that_aren_t_a_list_of_stations_are_refused(string body)
    {
        Assert.Throws<HoursFormatException>(() => Source().Parse(Encoding.UTF8.GetBytes(body)));
    }
}
