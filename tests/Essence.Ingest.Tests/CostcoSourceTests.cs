using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Essence.Hours;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Tests;

/// <summary>Costco's locator against a trimmed real answer (Fixtures/Hours/costco.json, fetched 2026-11-12 from Montréal).</summary>
public sealed class CostcoSourceTests
{
    private const string Laval = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";

    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hours", "costco.json"));

    private static CostcoSource Source(HttpMessageHandler? handler = null) =>
        new(new HoursClient(new HttpClient(handler ?? new StubHandler(_ => throw new InvalidOperationException("No request expected.")))),
            Options.Create(new HoursOptions()));

    [Fact]
    public void Laval_s_gas_bar_opens_at_6_and_closes_at_21_30_on_weekdays_and_20_30_on_weekends()
    {
        var parsed = Source().Parse(Fixture());

        var laval = Assert.Single(parsed.Records, r => r.Ref == "505");
        Assert.Equal(new SourceRecord("505", "costco", 45.574, -73.757, "2999", "H7P5P4", Laval, CostcoSource.Link, null, false), laval);
        Assert.Equal(OpeningHours.Parse(Laval), OpeningHours.Parse(laval.Rule!));
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void Only_Québec_s_warehouses_with_a_gas_bar_are_kept()
    {
        var answer = JsonNode.Parse(Fixture())!.AsArray();
        answer.Add(Warehouse(999, "QC", gas: false));

        var parsed = Source().Parse(Encoding.UTF8.GetBytes(answer.ToJsonString()));

        // Montréal and Laval; Gloucester, Ontario, and the warehouse without gas are left out.
        Assert.Equal(["515", "505"], parsed.Records.Select(r => r.Ref));
        Assert.All(parsed.Records, r => Assert.Equal(Laval, r.Rule));
    }

    [Fact]
    public void Hours_that_can_t_be_read_for_sure_leave_the_warehouse_out_with_a_warning()
    {
        var answer = new JsonArray
        {
            true,
            Warehouse(501, "QC", hours: [("Mon-Fri. ", "open", "6:00am - noon"), ("Sat. ", "open", "6:00am - 8:30pm"), ("Sun. ", "open", "6:00am - 8:30pm")]),
            Warehouse(502, "QC", hours: [("Mon-Fri. ", "open", "6:00am - 9:30pm"), ("Sat. ", "open", "6:00am - 8:30pm")]),
            Warehouse(503, "QC"),
        };

        var parsed = Source().Parse(Encoding.UTF8.GetBytes(answer.ToJsonString()));

        Assert.Equal("503", Assert.Single(parsed.Records).Ref);
        Assert.Collection(
            parsed.Warnings,
            w => Assert.Contains("Costco #501", w),
            w => Assert.Contains("Sunday's hours aren't given", w));
    }

    [Theory]
    [InlineData("<html><body>Access Denied</body></html>")]
    [InlineData("[false]")]
    [InlineData("{\"stlocID\":505}")]
    [InlineData("")]
    public void An_answer_that_isn_t_the_list_of_warehouses_is_refused(string body)
    {
        Assert.Throws<HoursFormatException>(() => Source().Parse(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task A_run_asks_from_montréal_and_québec_once_each_and_keeps_each_warehouse_once_without_people_s_details()
    {
        var answer = JsonNode.Parse(Fixture())!.AsArray();
        foreach (var warehouse in answer.OfType<JsonObject>())
        {
            // What the real answer has, and must not be kept.
            warehouse["manager"] = "A. PERSON MGR";
            warehouse["phone"] = "(555) 555-0100";
        }

        var requests = new List<Uri>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "text/html"),
            };
        });
        var source = Source(handler);

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(2, requests.Count);
        Assert.Contains("latitude=45.5&longitude=-73.57&countryCode=CA", requests[0].Query);
        Assert.Contains("latitude=46.81&longitude=-71.21&countryCode=CA", requests[1].Query);
        Assert.StartsWith("https://www.costco.ca/AjaxWarehouseBrowseLookupView?", requests[0].AbsoluteUri);
        var kept = Encoding.UTF8.GetString(fetched.Raw);
        Assert.DoesNotContain("manager", kept, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("555", kept);
        Assert.Equal(HoursVersion.Of(fetched.Raw), fetched.Version);
        // Both answers have the same warehouses: each is kept once.
        Assert.Equal(["515", "505"], source.Parse(fetched.Raw).Records.Select(r => r.Ref));
    }

    [Fact]
    public async Task The_same_answer_as_the_one_kept_isn_t_modified()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture()) });
        var source = Source(handler);
        var first = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        var second = await source.FetchAsync(first.Version, TestContext.Current.CancellationToken);

        Assert.IsType<HoursFetch.NotModified>(second);
    }

    [Fact]
    public async Task A_request_refused_fails_the_run()
    {
        var source = Source(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchAsync(null, TestContext.Current.CancellationToken));
    }

    private static JsonObject Warehouse(
        int id, string state, bool gas = true, (string Title, string Code, string Time)[]? hours = null)
    {
        var week = new JsonArray();
        foreach (var (title, code, time) in hours ?? [("Mon-Fri. ", "open", "6:00am - 9:30pm"), ("Sat. ", "open", "6:00am - 8:30pm"), ("Sun. ", "open", "6:00am - 8:30pm")])
        {
            week.Add(new JsonObject { ["title"] = title, ["code"] = code, ["time"] = time });
        }

        return new JsonObject
        {
            ["stlocID"] = id,
            ["locationName"] = $"Test {id}",
            ["address1"] = $"{id} RUE PRINCIPALE",
            ["state"] = state,
            ["country"] = "CA",
            ["zipCode"] = "H0H 0H0",
            ["latitude"] = 46.0,
            ["longitude"] = -73.0,
            ["hasGasDepartment"] = gas,
            ["gasStationHours"] = week,
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
