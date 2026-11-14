using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Essence.Ingest.Hours;
using static Essence.Ingest.Tests.HoursFixtures;

namespace Essence.Ingest.Tests;

/// <summary>
/// OpenStreetMap's fuel stations against a trimmed real Overpass answer (Fixtures/Hours/overpass.json, Québec's
/// <c>amenity=fuel</c> on 2026-11-13, <c>out tags center</c>: no one who mapped them, and no phone numbers).
/// </summary>
public sealed class OverpassSourceTests
{
    /// <summary>The feed's brands: not "Essence", the name of a station of the fixture's far north.</summary>
    private static readonly string[] Brands = ["Costco", "Ultramar", "Esso", "Irving", "Sonic", "Petro-Canada", "Harnois", "Couche-Tard", "R.L."];

    /// <summary>One feed station of each brand.</summary>
    private static readonly Stations FeedBrands =
        new([.. Brands.Select((brand, i) => Station(i + 1, $"{i + 1} rue Principale", "H1A 1A1", brand, 45.5, -73.5))]);

    private static OverpassSource Source(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) =>
        new(new OverpassClient(new HttpClient(new StubHandler(respond ?? (_ => throw new InvalidOperationException("No request expected."))))),
            FeedBrands, Options());

    private static HoursParse Parse() => Source().Parse(Bytes("overpass.json"));

    private static SourceRecord Record(string reference) => Assert.Single(Parse().Records, r => r.Ref == reference);

    [Fact]
    public void Every_element_is_a_record_with_hours_or_not_so_a_station_knows_which_to_edit()
    {
        var parsed = Parse();

        Assert.Equal(12, parsed.Records.Count);
        // Costco Laval's pumps: no hours in OpenStreetMap.
        Assert.Equal(
            new SourceRecord("node/6756481766", "costco", 45.575341, -73.7552574, null, null, null, "https://www.openstreetmap.org/node/6756481766", null, false)
            {
                Brand = "Costco",
            },
            Record("node/6756481766"));
    }

    [Fact]
    public void Hours_are_read_leniently_and_made_canonical()
    {
        Assert.Equal("24/7", Record("node/1907684666").Rule);
        Assert.Equal("Mo-Fr 06:00-23:00; Sa-Su 06:30-23:00", Record("node/799943379").Rule);
        // "," between the rules, as some mappers write it.
        Assert.Equal("Mo-Fr 05:00-24:00; Sa-Su 06:00-24:00", Record("node/5333182239").Rule);
    }

    [Fact]
    public void Hours_that_can_t_be_read_for_sure_leave_the_record_without_them_and_a_warning()
    {
        var parsed = Parse();

        var irving = Assert.Single(parsed.Records, r => r.Ref == "node/277667516");
        Assert.Null(irving.Rule);
        Assert.Equal("irving", irving.BrandFamily);
        Assert.Contains("OpenStreetMap node/277667516: its opening_hours \"Mo-Su,PH 08:00+\" can't be read for sure", Assert.Single(parsed.Warnings));
    }

    [Fact]
    public void The_brand_is_the_brand_tag_else_the_operator_else_the_name()
    {
        Assert.Equal("petro-canada", Record("way/26630403").BrandFamily);
        // No brand: "Ultramar" as operator, "Pétro-Canada" too.
        Assert.Equal("ultramar", Record("way/368505563").BrandFamily);
        Assert.Equal("petro-canada", Record("node/4320427121").BrandFamily);
        // A name only.
        Assert.Equal("harnois", Record("node/5334869289").BrandFamily);
        Assert.Equal("essence", Record("node/547557657").BrandFamily);
    }

    [Fact]
    public void The_brand_read_is_kept_as_written_for_the_app_to_tell_a_name_that_s_the_brand_s()
    {
        Assert.Equal("Petro-Canada", Record("way/26630403").Brand);
        Assert.Equal("Ultramar", Record("way/368505563").Brand);
        Assert.Equal("Pétro-Canada", Record("node/4320427121").Brand);
        // A name only, the brand's: "Harnois" is named after its brand.
        Assert.Equal("Harnois", Record("node/5334869289").Brand);
    }

    [Fact]
    public void A_way_is_at_its_centre_and_its_address_confirms_a_match()
    {
        var brossard = Record("way/26630403");

        Assert.Equal((45.4589862, -73.4642534, "7405", "J4Y1A2"), (brossard.Latitude!.Value, brossard.Longitude!.Value, brossard.HouseNumber!, brossard.Postcode!));
        Assert.Equal("https://www.openstreetmap.org/way/26630403", brossard.Link);
    }

    [Fact]
    public void The_hours_were_checked_on_check_date_opening_hours_else_on_check_date_and_otherwise_it_isn_t_known()
    {
        // Midnight in Québec: 04:00 UTC in daylight time, 05:00 in standard time.
        Assert.Equal(new DateTimeOffset(2025, 9, 1, 4, 0, 0, TimeSpan.Zero), Record("node/799943379").CheckedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 4, 0, 0, TimeSpan.Zero), Record("node/5360716343").CheckedAt);
        Assert.Equal(new DateTimeOffset(2023, 4, 21, 4, 0, 0, TimeSpan.Zero), Record("node/1907684666").CheckedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 20, 5, 0, 0, TimeSpan.Zero), Record("way/462637873").CheckedAt);
        Assert.Null(Record("node/6756481766").CheckedAt);
    }

    [Fact]
    public void A_check_date_of_a_month_is_its_first_day_and_one_that_isn_t_a_date_isn_t_a_check()
    {
        var answer = Json("overpass.json");
        var tags = answer["elements"]!.AsArray().OfType<JsonObject>().Single(e => (long)e["id"]! == 799943379)["tags"]!.AsObject();
        tags["check_date:opening_hours"] = "2025-09";
        var irving = answer["elements"]!.AsArray().OfType<JsonObject>().Single(e => (long)e["id"]! == 1907684666)["tags"]!.AsObject();
        irving["check_date"] = "last summer";

        var parsed = Source().Parse(Utf8(answer));

        Assert.Equal(new DateTimeOffset(2025, 9, 1, 4, 0, 0, TimeSpan.Zero), parsed.Records.Single(r => r.Ref == "node/799943379").CheckedAt);
        Assert.Null(parsed.Records.Single(r => r.Ref == "node/1907684666").CheckedAt);
    }

    [Fact]
    public void An_element_without_a_point_is_left_out_with_a_warning_rather_than_taken_for_its_brand_s_rule()
    {
        var answer = Json("overpass.json");
        answer["elements"]!.AsArray().Add(new JsonObject { ["type"] = "relation", ["id"] = 4680203, ["tags"] = new JsonObject { ["brand"] = "Shell", ["opening_hours"] = "24/7" } });

        var parsed = Source().Parse(Utf8(answer));

        Assert.DoesNotContain(parsed.Records, r => r.Ref == "relation/4680203");
        Assert.Contains(parsed.Warnings, w => w == "OpenStreetMap relation/4680203 has no point; left out.");
    }

    [Theory]
    [InlineData("<html><body>Too busy</body></html>")]
    [InlineData("{\"osm3s\":{}}")]
    [InlineData("""{"remark":"runtime error: Query timed out in \"query\" at line 1 after 181 seconds.","elements":[]}""")]
    public void An_answer_that_isn_t_the_elements_or_says_the_query_failed_is_refused(string body)
    {
        Assert.Throws<HoursFormatException>(() => Source().Parse(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task The_query_is_posted_as_a_form_and_the_answer_kept_without_the_tags_not_read()
    {
        var answer = Json("overpass.json");
        foreach (var element in answer["elements"]!.AsArray().OfType<JsonObject>())
        {
            element["tags"]!["phone"] = "+1-555-555-0100";
            element["user"] = "a-mapper";
        }

        var posted = new List<(Uri Url, string Body)>();
        var source = Source(request =>
        {
            posted.Add((request.RequestUri!, request.Content!.ReadAsStringAsync().Result));
            return Ok(answer.ToJsonString());
        });

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        var (url, body) = Assert.Single(posted);
        Assert.Equal("https://overpass-api.de/api/interpreter", url.OriginalString);
        Assert.Equal(
            "data=" + Uri.EscapeDataString("""[out:json][timeout:180];area["ISO3166-2"="CA-QC"]->.qc;nwr["amenity"="fuel"](area.qc);out tags center;""").Replace("%20", "+"),
            body);
        var kept = Encoding.UTF8.GetString(fetched.Raw);
        Assert.DoesNotContain("555", kept);
        Assert.DoesNotContain("a-mapper", kept);
        Assert.DoesNotContain("wikidata", kept);
        Assert.Contains("2026-11-13T04:09:05Z", kept);
        Assert.Equal(
            Parse().Records.Select(r => r.Ref == "node/547557657" ? r with { BrandFamily = null, Brand = null } : r),
            source.Parse(fetched.Raw).Records);
    }

    [Fact]
    public async Task An_operator_or_a_name_that_s_no_feed_station_s_brand_isn_t_kept_since_it_may_be_a_person_s()
    {
        var answer = Json("overpass.json");
        var harnois = answer["elements"]!.AsArray().OfType<JsonObject>().Single(e => (long)e["id"]! == 5334869289)["tags"]!.AsObject();
        harnois["name"] = "Garage Untel";
        var source = Source(_ => Ok(answer.ToJsonString()));

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Untel", Encoding.UTF8.GetString(fetched.Raw));
        Assert.Contains("\"Ultramar\"", Encoding.UTF8.GetString(fetched.Raw));
        var garage = source.Parse(fetched.Raw).Records.Single(r => r.Ref == "node/5334869289");
        Assert.Null(garage.BrandFamily);
        Assert.Null(garage.Brand);
    }

    [Fact]
    public async Task R_L_s_stations_named_after_Petroles_R_L_or_its_Rezzo_banner_keep_that_name_and_are_R_L_s()
    {
        var answer = Json("overpass.json");
        answer["elements"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "node", ["id"] = 5309263899, ["lat"] = 48.3366, ["lon"] = -70.8843,
            ["tags"] = new JsonObject { ["amenity"] = "fuel", ["name"] = "Petroles RL", ["addr:housenumber"] = "1162", ["addr:postcode"] = "G7B 1C5" },
        });
        answer["elements"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "way", ["id"] = 549565977, ["center"] = new JsonObject { ["lat"] = 48.2353, ["lon"] = -70.5841 },
            ["tags"] = new JsonObject { ["amenity"] = "fuel", ["name"] = "Pétrole Rl" },
        });
        answer["elements"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "node", ["id"] = 10909434881, ["lat"] = 48.2470, ["lon"] = -70.8500,
            ["tags"] = new JsonObject { ["amenity"] = "fuel", ["brand"] = "Rezzo" },
        });
        var source = Source(_ => Ok(answer.ToJsonString()));

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));
        var records = source.Parse(fetched.Raw).Records;

        string[] named = ["node/5309263899", "way/549565977", "node/10909434881"];
        Assert.Equal(["r-l", "r-l", "r-l"], named.Select(r => records.Single(e => e.Ref == r).BrandFamily));
        Assert.Contains("Petroles RL", Encoding.UTF8.GetString(fetched.Raw));
    }

    [Theory]
    [InlineData("504")]
    [InlineData("timeout")]
    [InlineData("remark")]
    [InlineData("empty")]
    public async Task A_server_busy_too_slow_or_answering_no_elements_gives_way_to_the_next(string failure)
    {
        var asked = new List<string>();
        var source = Source(request =>
        {
            asked.Add(request.RequestUri!.Host);
            if (asked.Count > 1)
            {
                return Ok(Bytes("overpass.json"));
            }

            return failure switch
            {
                "504" => new HttpResponseMessage(HttpStatusCode.GatewayTimeout),
                "remark" => Ok("""{"osm3s":{},"remark":"runtime error: Query run out of memory using about 2048 MB of RAM.","elements":[]}"""),
                // A server without its areas, or rebuilding them: no error, no elements.
                "empty" => Ok("""{"version":0.6,"osm3s":{"timestamp_osm_base":"2026-11-13T04:09:05Z"},"elements":[]}"""),
                _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
            };
        });

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(["overpass-api.de", "overpass.private.coffee"], asked);
        Assert.Equal(12, source.Parse(fetched.Raw).Records.Count);
    }

    [Fact]
    public async Task Every_server_failing_fails_the_run_saying_why_each_did()
    {
        var source = Source(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("https://overpass-api.de/api/interpreter: ", error.Message);
        Assert.Contains("https://overpass.private.coffee/api/interpreter: ", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task Shutting_down_stops_at_the_server_asked()
    {
        using var cancellation = new CancellationTokenSource();
        var asked = 0;
        var source = Source(_ =>
        {
            asked++;
            cancellation.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(null, cancellation.Token));
        Assert.Equal(1, asked);
    }
}
