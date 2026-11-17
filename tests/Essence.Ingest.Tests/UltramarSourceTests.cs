using System.Net;
using System.Text.Json.Nodes;
using Essence.Ingest.Hours;
using static Essence.Ingest.Tests.HoursFixtures;

namespace Essence.Ingest.Tests;

/// <summary>Ultramar's locator against a trimmed real answer (Fixtures/Hours/ultramar.json, fetched 2026-11-12).</summary>
public sealed class UltramarSourceTests
{
    private static UltramarSource Source(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) => new(Client(respond), Options());

    private static HoursParse Parse(JsonNode answer) => Source().Parse(Utf8(answer));

    private static JsonObject Site(JsonNode answer, string id) =>
        answer["data"]!.AsArray().OfType<JsonObject>().Single(s => (string?)s["locationId"] == id);

    private static JsonObject Forecourt(JsonObject site) =>
        site["services"]!.AsArray().OfType<JsonObject>().Single(s => (string?)s["serviceType"] == "Forecourt");

    private static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private static JsonArray Week(string open, string close) =>
        new([.. Days.Select(day => (JsonNode)new JsonObject { ["dayOfWeek"] = day, ["openTime"] = open, ["closeTime"] = close })]);

    [Fact]
    public void A_station_s_days_are_its_week_confirmed_by_its_postal_code_and_number()
    {
        var parsed = Source().Parse(Bytes("ultramar.json"));

        Assert.Equal(
            new SourceRecord("1581", "ultramar", 46.786362, -71.3017014, "2631", "G1V1A3", "Mo-Su 06:00-22:00", UltramarSource.Link, null, false),
            Assert.Single(parsed.Records, r => r.Ref == "1581"));
        Assert.Equal("24/7", Assert.Single(parsed.Records, r => r.Ref == "1399").Rule);
    }

    [Fact]
    public void A_station_closed_for_now_whose_forecourt_is_active_is_left_out_since_which_is_right_can_t_be_told()
    {
        var answer = Json("ultramar.json");

        // The real answer: the site and its store say "temporary closure", the forecourt "active", as at every station.
        var parsed = Parse(answer);
        Assert.DoesNotContain(parsed.Records, r => r.Ref == "1689");
        Assert.Contains(
            "Ultramar 1689 (313 4e Rue de la Pointe, SHAWINIGAN): it says it's closed for now, but its forecourt says it's active: which is right can't be told; left out.",
            parsed.Warnings);

        Forecourt(Site(answer, "1689"))["serviceStatus"] = "temporary closure";
        Assert.True(Assert.Single(Parse(answer).Records, r => r.Ref == "1689").TemporarilyClosed);
    }

    [Fact]
    public void A_forecourt_closed_for_now_closes_the_station_whatever_the_station_says()
    {
        var answer = Json("ultramar.json");
        Forecourt(Site(answer, "1581"))["serviceStatus"] = "temporary closure";

        Assert.True(Assert.Single(Parse(answer).Records, r => r.Ref == "1581").TemporarilyClosed);
    }

    [Fact]
    public void Without_a_forecourt_the_site_s_status_counts()
    {
        var answer = Json("ultramar.json");
        var site = Site(answer, "1689");
        site["services"] = new JsonArray();

        Assert.True(Assert.Single(Parse(answer).Records, r => r.Ref == "1689").TemporarilyClosed);
    }

    [Fact]
    public void The_forecourt_s_hours_come_before_the_station_s()
    {
        var answer = Json("ultramar.json");
        var site = Site(answer, "1581");
        Forecourt(site)["serviceRegularHours"] = Week("07:00", "21:00");

        Assert.Equal("Mo-Su 07:00-21:00", Assert.Single(Parse(answer).Records, r => r.Ref == "1581").Rule);

        site["services"] = new JsonArray();
        Assert.Equal("Mo-Su 06:00-22:00", Assert.Single(Parse(answer).Records, r => r.Ref == "1581").Rule);
    }

    [Fact]
    public void A_forecourt_that_gives_no_hours_gives_way_to_the_station_s_and_the_warning_says_whose_can_t_be_read()
    {
        var answer = Json("ultramar.json");
        var site = Site(answer, "1581");
        Forecourt(site)["serviceRegularHours"] = new JsonArray();
        Forecourt(site)["serviceTwentyFourSeven"] = false;

        Assert.Equal("Mo-Su 06:00-22:00", Assert.Single(Parse(answer).Records, r => r.Ref == "1581").Rule);

        site["siteRegularHours"] = Week("00:00", "12:00");
        var parsed = Parse(answer);

        Assert.DoesNotContain(parsed.Records, r => r.Ref == "1581");
        Assert.Contains(parsed.Warnings, w => w.StartsWith("Ultramar 1581 (2631 ", StringComparison.Ordinal) && w.Contains("): the station's hours can't be read for sure (Monday's hours"));
    }

    [Fact]
    public void Hours_that_contradict_24_hours_or_are_the_locator_s_placeholder_leave_the_station_out_and_Ontario_s_are_left_out()
    {
        var parsed = Source().Parse(Bytes("ultramar.json"));

        Assert.Equal(["1581", "1399"], parsed.Records.Select(r => r.Ref));
        Assert.Collection(
            parsed.Warnings,
            w => Assert.Contains("Ultramar 1689 (313 4e Rue de la Pointe, SHAWINIGAN): it says it's closed for now", w),
            w => Assert.Contains("Ultramar 1375 (1031 Bd Pie-XI S, QUÉBEC): the forecourt's hours can't be read for sure (it says it's open 24 hours, but its days say Mo-Su 06:00-23:00)", w),
            w => Assert.Contains("Ultramar 1716 (1890 Bd du Curé-Labelle, LAVAL): the forecourt's hours can't be read for sure (Sunday's hours are \"00:00\" to \"12:00\"", w));
    }

    [Fact]
    public void Open_all_day_without_the_24_hours_flag_is_24_7_and_a_status_not_understood_leaves_the_station_out()
    {
        var answer = Json("ultramar.json");
        var site = Site(answer, "1581");
        Forecourt(site)["serviceRegularHours"] = Week("00:00", "23:59");
        Site(answer, "1399")["services"] = new JsonArray();
        Site(answer, "1399")["siteStatus"] = "permanently closed";

        var parsed = Parse(answer);

        Assert.Equal("24/7", Assert.Single(parsed.Records, r => r.Ref == "1581").Rule);
        Assert.DoesNotContain(parsed.Records, r => r.Ref == "1399");
        Assert.Contains(parsed.Warnings, w => w.Contains("1399") && w.Contains("\"permanently closed\" isn't understood"));
    }

    [Theory]
    [InlineData("<html></html>")]
    [InlineData("{\"success\":false,\"data\":[]}")]
    [InlineData("[]")]
    public void An_answer_that_isn_t_the_list_of_stations_is_refused(string body)
    {
        Assert.Throws<HoursFormatException>(() => Source().Parse(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task A_run_asks_once_and_keeps_only_the_fields_read()
    {
        var answer = Json("ultramar.json");
        Forecourt(Site(answer, "1581"))["serviceLoyaltyBrand"] = "Journie Reward";
        var requests = new List<Uri>();
        var source = Source(request =>
        {
            requests.Add(request.RequestUri!);
            return Ok(answer.ToJsonString());
        });

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(
            "https://journie.ca/ultramar/api/locations/nearest?country=CA&pageSize=1000&lat=46.8&lon=-71.3&range=100000000&brand=Ultramar",
            Assert.Single(requests).OriginalString);
        Assert.DoesNotContain("Journie Reward", System.Text.Encoding.UTF8.GetString(fetched.Raw));
        Assert.Equal(Source().Parse(Bytes("ultramar.json")), source.Parse(fetched.Raw), new ParseComparer());
    }

    [Fact]
    public async Task An_answer_with_more_pages_is_refused_rather_than_missing_stations()
    {
        var answer = Json("ultramar.json");
        answer["pagination"]!["hasNextPage"] = true;
        var source = Source(_ => Ok(answer.ToJsonString()));

        await Assert.ThrowsAsync<HoursFormatException>(() => source.FetchAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_refused_fails_the_run()
    {
        var source = Source(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(null, TestContext.Current.CancellationToken));
    }

    private sealed class ParseComparer : IEqualityComparer<HoursParse>
    {
        public bool Equals(HoursParse? x, HoursParse? y) =>
            x is not null && y is not null && x.Records.SequenceEqual(y.Records) && x.Warnings.SequenceEqual(y.Warnings);

        public int GetHashCode(HoursParse obj) => obj.Records.Count;
    }
}
