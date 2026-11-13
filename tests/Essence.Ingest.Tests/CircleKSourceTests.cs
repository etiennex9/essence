using System.Net;
using System.Text.Json.Nodes;
using Essence.Ingest.Hours;
using static Essence.Ingest.Tests.HoursFixtures;

namespace Essence.Ingest.Tests;

/// <summary>Circle K's locator against a trimmed real answer (Fixtures/Hours/circlek.json, Québec's west box on 2026-11-12).</summary>
public sealed class CircleKSourceTests
{
    private static CircleKSource Source(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) =>
        new(Client(respond), NoPause(), Options());

    private static HoursParse Parse(JsonNode answer) => Source().Parse(Utf8(answer));

    [Fact]
    public void Couche_Tard_140_in_Laval_is_the_store_6000140_with_its_week()
    {
        var parsed = Source().Parse(Bytes("circlek.json"));

        var laval = Assert.Single(parsed.Records, r => r.Ref == "6000140");
        Assert.Equal(
            new SourceRecord("6000140", "couche-tard", 45.60074, -73.73205, "45", "H7M1E3", "Mo-Fr 05:00-23:00; Sa-Su 06:00-23:00", CircleKSource.Link, null, false),
            laval with { StoreNumber = null });
        Assert.Equal(140, laval.StoreNumber);
    }

    [Fact]
    public void A_store_always_open_is_24_7_and_hours_past_midnight_or_to_midnight_read_as_they_are()
    {
        var parsed = Source().Parse(Bytes("circlek.json"));

        Assert.Equal("24/7", Assert.Single(parsed.Records, r => r.Ref == "6001174").Rule);
        Assert.Equal("Mo-Fr 05:30-00:30; Sa-Su 06:30-00:30", Assert.Single(parsed.Records, r => r.Ref == "6000864").Rule);
        Assert.Equal("Mo-Su 05:00-24:00", Assert.Single(parsed.Records, r => r.Ref == "6000507").Rule);
    }

    [Fact]
    public void A_store_without_hours_or_with_a_day_missing_is_left_out_with_a_warning_and_Ontario_s_are_left_out()
    {
        var parsed = Source().Parse(Bytes("circlek.json"));

        Assert.Equal(["6000140", "6001174", "6000864", "6000507"], parsed.Records.Select(r => r.Ref));
        Assert.Collection(
            parsed.Warnings,
            w => Assert.Contains("6000206 (800 PLACE VICTORIA, Montréal): the store's hours can't be read for sure (Sunday's hours aren't given)", w),
            w => Assert.Contains("6000276 (9301 BOUL. LASALLE, LASALLE): the store's hours can't be read for sure (it gives none)", w));
    }

    [Fact]
    public void The_fuel_s_hours_come_first_when_a_store_gives_them()
    {
        var answer = Json("circlek.json").AsArray();
        var store = answer.OfType<JsonObject>().Single(s => (string?)s["id"] == "6000140");
        store["openingHoursFuel"] = new JsonObject { ["alwaysOpen"] = true };

        Assert.Equal("24/7", Assert.Single(Parse(answer).Records, r => r.Ref == "6000140").Rule);
    }

    [Theory]
    [InlineData("6000140", 140)]
    [InlineData("6000001", 1)]
    [InlineData("6999999", 999999)]
    [InlineData("6000000", null)]
    [InlineData("801757", null)]
    [InlineData("7000000", null)]
    [InlineData("60001x", null)]
    public void A_store_s_number_is_its_id_past_6000000(string id, int? number)
    {
        Assert.Equal(number, CircleKSource.StoreNumber(id));
    }

    [Fact]
    public void A_store_not_numbered_as_Québec_s_is_left_out_with_a_warning()
    {
        var answer = Json("circlek.json").AsArray();
        answer.OfType<JsonObject>().Single(s => (string?)s["id"] == "6001174")["id"] = "801999";

        var parsed = Parse(answer);

        Assert.DoesNotContain(parsed.Records, r => r.Ref == "801999");
        Assert.Contains(parsed.Warnings, w => w.Contains("801999") && w.Contains("isn't numbered"));
    }

    [Theory]
    [InlineData("<html><body>Access Denied</body></html>")]
    [InlineData("{\"stations\":[]}")]
    [InlineData("")]
    public void An_answer_that_isn_t_the_list_of_stores_is_refused(string body)
    {
        Assert.Throws<HoursFormatException>(() => Source().Parse(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task A_run_asks_about_each_box_once_and_keeps_each_store_once_without_its_phone()
    {
        var answer = Json("circlek.json").AsArray();
        foreach (var store in answer.OfType<JsonObject>())
        {
            store["phoneNumber"] = "+15555550100  ";
        }

        var requests = new List<Uri>();
        var source = Source(request =>
        {
            requests.Add(request.RequestUri!);
            return Ok(answer.ToJsonString());
        });

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(2, requests.Count);
        Assert.Equal(
            "https://api.circlek.com/us/ngrp-store-locator/v1/stations?topLeftLatitude=62.6&topLeftLongitude=-79.8&bottomRightLatitude=44.9"
            + "&bottomRightLongitude=-71.5&maxResults=2000&country=CA&brand=CIRCLEK,COUCHE_TARD",
            requests[0].OriginalString);
        Assert.Contains("topLeftLongitude=-71.5&bottomRightLatitude=44.9&bottomRightLongitude=-57.0", requests[1].OriginalString);
        Assert.DoesNotContain("555", System.Text.Encoding.UTF8.GetString(fetched.Raw));
        Assert.Equal(HoursVersion.Of(fetched.Raw), fetched.Version);
        Assert.Equal(["6000140", "6001174", "6000864", "6000507"], source.Parse(fetched.Raw).Records.Select(r => r.Ref));
        Assert.IsType<HoursFetch.NotModified>(await source.FetchAsync(fetched.Version, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_refused_fails_the_run()
    {
        var source = Source(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(null, TestContext.Current.CancellationToken));
    }
}
