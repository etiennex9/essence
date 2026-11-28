using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Essence.Ingest.Hours;
using Essence.Ingest.Hours.Plugins;
using static Essence.Ingest.Tests.HoursFixtures;

namespace Essence.Ingest.Tests;

/// <summary>
/// The plugins' engine end to end, on trimmed real answers (Fixtures/HoursPlugins: Crevier's on journie.ca and Harnois
/// Énergies', fetched 2026-11-24, phones dropped) and made-up ones (a CSV file of open data, a page with schema.org data).
/// </summary>
public sealed class HoursPluginSourceTests
{
    private static byte[] Plugin(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HoursPlugins", name));

    private static HoursPluginSpec Spec(string json) => HoursPluginSpec.Parse(Encoding.UTF8.GetBytes(json), "test");

    private static HoursPluginSpec Shipped(string name) => HoursPluginCatalog.Load(null, new HashSet<string>()).Find(name)!;

    private static HoursPluginSource Source(HoursPluginSpec spec, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(spec, Client(respond), NoPause(), Options());

    private static async Task<HoursParse> RunAsync(HoursPluginSpec spec, byte[] answer)
    {
        var source = Source(spec, _ => Ok(answer));
        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, CancellationToken.None));
        return source.Parse(fetched.Raw);
    }

    [Fact]
    public async Task Crevier_s_forecourts_are_read_its_placeholder_days_leave_a_station_out_and_Ontario_s_are_left_out()
    {
        var parsed = await RunAsync(Shipped("crevier"), Plugin("crevier.json"));

        Assert.Equal(["52614", "52792"], parsed.Records.Select(r => r.Ref).Order());
        var record = Assert.Single(parsed.Records, r => r.Ref == "52792");
        Assert.Equal(("crevier", "Mo-Su 06:00-20:00", "959", "G0S2C0", "https://journie.ca/ultramar/qc-fr"), (record.BrandFamily, record.Rule, record.HouseNumber, record.Postcode, record.Link));
        Assert.Equal("24/7", Assert.Single(parsed.Records, r => r.Ref == "52614").Rule);
        Assert.Contains(parsed.Warnings, w => w.StartsWith("Crevier 52545 (1970 Bd Mercure): the forecourt's hours can't be read for sure", StringComparison.Ordinal)
            && w.Contains("00:00-12:00, which the source gives when it doesn't say", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed.Records, r => r.Ref == "52789");
    }

    [Fact]
    public async Task Harnois_gives_24_7_to_the_stations_with_its_24_hours_service_and_nothing_to_the_others()
    {
        var parsed = await RunAsync(Shipped("harnois"), Plugin("harnois.json"));

        var record = Assert.Single(parsed.Records);
        Assert.Equal(("1342", "harnois", "24/7", "950", "G7H4P3"), (record.Ref, record.BrandFamily, record.Rule, record.HouseNumber, record.Postcode));
        Assert.StartsWith("https://harnoisenergies.com/fr/station/", record.Link, StringComparison.Ordinal);
        Assert.Equal(["2 of the 3 places give no hours; left out."], parsed.Warnings);
    }

    [Fact]
    public async Task Ultramar_s_locator_as_a_plugin_reads_what_its_own_source_reads()
    {
        // The template for turning a source written in code into a plugin: the same records from the same answer.
        var spec = Spec($$"""
            {
              "name": "ultramar-plugin", "label": "Ultramar", "brand": "Ultramar", "link": "{{UltramarSource.Link}}",
              "url": "https://example.com/ultramar", "hours": { "unknown": ["00:00-12:00"] }
            }
            """);

        var plugin = await RunAsync(spec, Bytes("ultramar.json"));
        var own = new UltramarSource(Client(), Options()).Parse(Bytes("ultramar.json"));

        Assert.Equal(own.Records.OrderBy(r => r.Ref), plugin.Records.OrderBy(r => r.Ref));
        Assert.Equal(own.Warnings.Count, plugin.Warnings.Count);
    }

    [Theory]
    [InlineData("temporary closure", "active", null)]
    [InlineData("temporary closure", "temporary closure", true)]
    [InlineData("active", "temporary closure", true)]
    [InlineData("active", "gone fishing", null)]
    public async Task A_forecourt_s_status_comes_first_and_a_station_closed_for_now_whose_forecourt_is_open_is_left_out(
        string siteStatus, string forecourtStatus, bool? closed)
    {
        var answer = Json("ultramar.json");
        var site = answer["data"]!.AsArray().OfType<JsonObject>().Single(s => (string?)s["locationId"] == "1581");
        site["siteStatus"] = siteStatus;
        site["services"]!.AsArray().OfType<JsonObject>().Single(s => (string?)s["serviceType"] == "Forecourt")["serviceStatus"] = forecourtStatus;
        var spec = Spec("""{ "name": "ultramar-plugin", "label": "Ultramar", "url": "https://example.com/u", "hours": { "unknown": ["00:00-12:00"] } }""");

        var record = (await RunAsync(spec, Utf8(answer))).Records.SingleOrDefault(r => r.Ref == "1581");

        Assert.Equal(closed, record?.TemporarilyClosed);
    }

    [Fact]
    public async Task Open_data_in_a_CSV_file_is_read_from_its_French_columns_and_hours()
    {
        var spec = Spec("""{ "name": "ville", "label": "Données ouvertes", "kind": "open-data", "url": "https://example.com/stations.csv" }""");

        var parsed = await RunAsync(spec, Plugin("open-data.csv"));

        Assert.Collection(
            parsed.Records,
            r => Assert.Equal(("46.81234,-71.21234", "exemple", "125", "G0A1A0", "Mo-Fr 06:00-22:00; Sa-Su 07:00-21:00"), (r.Ref, r.BrandFamily, r.HouseNumber, r.Postcode, r.Rule)),
            r => Assert.Equal(("2000", "J8X1A1", "24/7"), (r.HouseNumber, r.Postcode, r.Rule)));
        Assert.Contains(parsed.Warnings, w => w.Contains("77 chemin de la Rivière", StringComparison.Ordinal) && w.Contains("\"sauf\" isn't understood", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_s_schema_org_stations_are_read_their_season_s_hours_skipped()
    {
        var spec = Spec("""{ "name": "exemple", "label": "Exemple", "brand": "Exemple", "url": "https://example.com/stations/" }""");

        var parsed = await RunAsync(spec, Plugin("stores.html"));

        Assert.Collection(
            parsed.Records.OrderBy(r => r.Ref),
            r => Assert.Equal(("https://example.com/stations/101", "Mo-Fr 06:00-22:00; Sa-Su 07:00-21:00", "3000", "H7P5P4", "https://example.com/stations/101"), (r.Ref, r.Rule, r.HouseNumber, r.Postcode, r.Link)),
            r => Assert.Equal(("https://example.com/stations/102", "Mo-Fr 05:30-23:00; Sa-Su 07:00-23:00", "exemple"), (r.Ref, r.Rule, r.BrandFamily)));
    }

    [Fact]
    public async Task Requests_by_params_are_paced_and_their_records_put_together_each_once()
    {
        var asked = new List<Uri>();
        var spec = Spec("""
            {
              "name": "crevier-points", "label": "Crevier", "brand": "Crevier", "url": "https://example.com/near?lat={lat}&lon={lon}",
              "params": [{ "lat": 45.5, "lon": -73.57 }, { "lat": 46.81, "lon": -71.21 }], "hours": { "unknown": ["00:00-12:00"] }
            }
            """);
        var source = Source(spec, request =>
        {
            asked.Add(request.RequestUri!);
            return Ok(Plugin("crevier.json"));
        });

        var fetched = Assert.IsType<HoursFetch.Fetched>(await source.FetchAsync(null, CancellationToken.None));

        Assert.Equal(["https://example.com/near?lat=45.5&lon=-73.57", "https://example.com/near?lat=46.81&lon=-71.21"], asked.Select(u => u.ToString()));
        Assert.Equal(2, source.Parse(fetched.Raw).Records.Count);
        Assert.IsType<HoursFetch.NotModified>(await source.FetchAsync(fetched.Version, CancellationToken.None));
    }

    [Fact]
    public async Task Filters_keep_only_the_records_they_say()
    {
        var spec = Spec("""{ "name": "harnois-only", "label": "Harnois", "url": "https://example.com/s", "fields": { "id": "harnoisID" }, "where": { "brandKey": ["petrot", "esso"] } }""");

        var parsed = await RunAsync(spec, Plugin("harnois.json"));

        Assert.Empty(parsed.Records);
        Assert.Equal(["2 of the 2 places give no hours; left out."], parsed.Warnings);
    }

    [Fact]
    public async Task An_answer_that_says_more_pages_follow_fails_the_run()
    {
        var answer = Json("ultramar.json");
        answer["pagination"] = new JsonObject { ["hasNextPage"] = true };
        var source = Source(Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u" }"""), _ => Ok(Utf8(answer)));

        var error = await Assert.ThrowsAsync<HoursFormatException>(() => source.FetchAsync(null, CancellationToken.None));

        Assert.Contains("more pages follow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_without_places_with_points_or_that_fails_fails_the_run()
    {
        var spec = Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u" }""");

        await Assert.ThrowsAsync<HoursFormatException>(() => Source(spec, _ => Ok("""{"data": [{"name": "x"}]}""")).FetchAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => Source(spec, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)).FetchAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task An_answer_that_says_it_failed_or_has_a_key_twice_fails_the_run()
    {
        var spec = Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u" }""");
        foreach (var said in new JsonNode?[] { false, "false", 0, "error", null })
        {
            var failed = Json("ultramar.json");
            failed["success"] = said?.DeepClone();

            var error = await Assert.ThrowsAsync<HoursFormatException>(() => Source(spec, _ => Ok(Utf8(failed))).FetchAsync(null, CancellationToken.None));
            Assert.Contains("says it failed", error.Message, StringComparison.Ordinal);
        }

        var succeeded = Json("ultramar.json");
        succeeded["success"] = "true";
        Assert.IsType<HoursFetch.Fetched>(await Source(spec, _ => Ok(Utf8(succeeded))).FetchAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<HoursFormatException>(() => Source(spec, _ => Ok("""{"stations": [{"id": "1", "id": "2", "lat": 46.8, "lon": -71.2}]}"""))
            .FetchAsync(null, CancellationToken.None));
    }

    [Theory]
    [InlineData("no site status")]
    [InlineData("a US site")]
    [InlineData("a French site")]
    [InlineData("temporarily closed, forecourt active")]
    public async Task Ultramar_s_locator_as_a_plugin_leaves_out_what_its_own_source_leaves_out(string mutation)
    {
        var spec = Spec($$"""
            {
              "name": "ultramar-plugin", "label": "Ultramar", "brand": "Ultramar", "link": "{{UltramarSource.Link}}",
              "url": "https://example.com/ultramar", "hours": { "unknown": ["00:00-12:00"] }
            }
            """);
        var answer = Json("ultramar.json");
        var site = (JsonObject)answer["data"]![0]!;
        switch (mutation)
        {
            case "no site status":
                site.Remove("siteStatus");
                break;
            case "a US site":
                site["siteCountry"] = "US";
                break;
            case "a French site":
                site["siteCountry"] = "FR";
                break;
            default:
                site["siteStatus"] = "temporary closure";
                break;
        }

        var plugin = await RunAsync(spec, Utf8(answer));
        var own = new UltramarSource(Client(), Options()).Parse(Utf8(answer));

        Assert.Equal(own.Records.OrderBy(r => r.Ref), plugin.Records.OrderBy(r => r.Ref));
        Assert.DoesNotContain(plugin.Records, r => r.Ref == (string)site["locationId"]! && mutation != "temporarily closed, forecourt active");
    }

    [Fact]
    public async Task Another_department_s_hours_and_24_hour_flags_are_never_the_station_s()
    {
        var spec = Spec("""{ "name": "test-d", "label": "D", "url": "https://example.com/d" }""");
        var answer = """
            {"stations": [
              {"id": "1", "lat": 46.81, "lon": -71.21, "hours": null, "carWashHours": "Lun-Dim 8h-20h"},
              {"id": "2", "lat": 46.82, "lon": -71.22, "hours": "Lun-Dim 6h-23h", "carWashHours": "Lun-Dim 8h-20h"},
              {"id": "3", "lat": 46.83, "lon": -71.23, "atm": {"open24h": true}, "carWash": {"services": ["24h"]}},
              {"id": "4", "lat": 46.84, "lon": -71.24, "hours": {"carWash": "Lun-Dim 8h-20h"}},
              {"id": "5", "lat": 46.85, "lon": -71.25, "info": {"atm": "24/7", "wifi": "oui"}},
              {"id": "6", "lat": 46.86, "lon": -71.26, "tags": [{"type": "Lave-auto", "name": "24h"}, {"type": "Air", "name": "Gratuit"}]}
            ]}
            """;

        var parsed = await RunAsync(spec, Encoding.UTF8.GetBytes(answer));

        var record = Assert.Single(parsed.Records);
        Assert.Equal(("2", "Mo-Su 06:00-23:00"), (record.Ref, record.Rule));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("D 4: its hours can't be read for sure", StringComparison.Ordinal) && w.Contains("\"carWash\" isn't a day", StringComparison.Ordinal));
        Assert.Contains("4 of the 6 places give no hours; left out.", parsed.Warnings);
    }

    [Fact]
    public async Task Only_the_station_s_own_hours_fields_and_tags_are_taken_any_other_department_s_needs_a_hint()
    {
        var spec = Spec("""{ "name": "test-t", "label": "T", "url": "https://example.com/t" }""");
        var answer = """
            {"stations": [
              {"id": "1", "lat": 46.81, "lon": -71.21, "services": {"12": "24hrs", "26": "Épicerie"}},
              {"id": "2", "lat": 46.82, "lon": -71.22, "services": {"epicerie": "24h", "timHortons": "24h", "driveThru": "24h", "air": "24h"}},
              {"id": "3", "lat": 46.83, "lon": -71.23, "info": {"abm": "24/7", "dab": "24/7"}},
              {"id": "4", "lat": 46.84, "lon": -71.24, "services": [{"type": "Tim Hortons", "name": "24h"}], "timHortonsHours": "Lun-Dim 6h-23h", "epicerieHours": "24/7"},
              {"id": "5", "lat": 46.85, "lon": -71.25, "location": {"hours": "Lun-Dim 6h-22h"}, "timHortons": {"open24h": true, "hours": "24/7"}}
            ]}
            """;

        var parsed = await RunAsync(spec, Encoding.UTF8.GetBytes(answer));

        Assert.Equal(["1 24/7", "5 Mo-Su 06:00-22:00"], parsed.Records.Select(r => $"{r.Ref} {r.Rule}").Order(StringComparer.Ordinal));
        Assert.Contains("3 of the 5 places give no hours; left out.", parsed.Warnings);
    }

    [Theory]
    [InlineData("gone fishing", "isn't understood")]
    [InlineData("", "gives no status")]
    public async Task A_status_the_reader_doesn_t_know_leaves_its_station_out_whatever_the_others_say(string status, string reason)
    {
        // One odd value among few used to drop the whole status field, and that station was published as open.
        var answer = JsonNode.Parse(Plugin("crevier.json"))!;
        var site = answer["data"]!.AsArray().OfType<JsonObject>().First(s => (string?)s["siteStateProvince"] == "QC" && (string?)s["locationId"] == "52792");
        site["siteStatus"] = status;

        var parsed = await RunAsync(Shipped("crevier"), Utf8(answer));

        Assert.DoesNotContain(parsed.Records, r => r.Ref == "52792");
        Assert.Contains(parsed.Warnings, w => w.StartsWith("Crevier 52792", StringComparison.Ordinal) && w.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Special_hours_that_aren_t_dated_refuse_the_week_and_dated_ones_are_the_holidays()
    {
        var spec = Spec("""{ "name": "test-s", "label": "S", "url": "https://example.com/s" }""");
        var answer = """
            [
              {"@type": "GasStation", "@id": "a", "geo": {"latitude": 46.81, "longitude": -71.21}, "openingHours": "Mo-Su 06:00-22:00",
               "specialOpeningHoursSpecification": [{"dayOfWeek": "Sunday", "opens": "00:00", "closes": "00:00"}]},
              {"@type": "GasStation", "@id": "b", "geo": {"latitude": 46.82, "longitude": -71.22}, "openingHours": "Mo-Su 06:00-22:00",
               "specialOpeningHoursSpecification": [{"validFrom": "2026-12-25", "validThrough": "2026-12-25", "opens": "00:00", "closes": "00:00"}]}
            ]
            """;

        var parsed = await RunAsync(spec, Encoding.UTF8.GetBytes(answer));

        Assert.Equal(("b", "Mo-Su 06:00-22:00"), (Assert.Single(parsed.Records).Ref, parsed.Records[0].Rule));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("S a:", StringComparison.Ordinal) && w.Contains("special hours aren't dated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fields_that_give_the_hours_must_agree_unless_one_is_the_fuel_s()
    {
        var spec = Spec("""{ "name": "test-a", "label": "A", "url": "https://example.com/a" }""");
        var answer = """
            {"places": [
              {"id": "1", "lat": 46.81, "lon": -71.21, "openingHours": "Mo-Su 06:00-22:00", "horaire": "Lun-Dim 7h-21h"},
              {"id": "2", "lat": 46.82, "lon": -71.22, "openingHours": "Mo-Su 06:00-22:00", "horaire": "Tous les jours 6h-22h"},
              {"id": "3", "lat": 46.83, "lon": -71.23, "openingHours": "Mo-Su 07:00-21:00", "fuelHours": "24/7"}
            ]}
            """;

        var parsed = await RunAsync(spec, Encoding.UTF8.GetBytes(answer));

        Assert.Equal(["2 Mo-Su 06:00-22:00", "3 24/7"], parsed.Records.Select(r => $"{r.Ref} {r.Rule}").Order(StringComparer.Ordinal));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("A 1: its hours can't be read for sure (two of its fields give different hours", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_s_block_with_a_key_twice_is_skipped_and_its_other_blocks_read()
    {
        var spec = Spec("""{ "name": "test-p", "label": "P", "url": "https://example.com/p" }""");
        var page = """
            <html><head>
            <script type="application/ld+json">{"@type": "Organization", "name": "X", "name": "Y"}</script>
            <script type="application/ld+json">{"@type": "GasStation", "@id": "s1", "geo": {"latitude": 46.8, "longitude": -71.2}, "openingHours": "Mo-Su 06:00-22:00"}</script>
            </head></html>
            """;

        var parsed = await RunAsync(spec, Encoding.UTF8.GetBytes(page));

        Assert.Equal(("s1", "Mo-Su 06:00-22:00"), (Assert.Single(parsed.Records).Ref, parsed.Records[0].Rule));
    }

    [Fact]
    public void A_page_whose_scripts_never_close_is_read_in_one_pass()
    {
        var page = string.Concat(Enumerable.Repeat("""<script type="application/json">x""", 200_000));

        Assert.Empty(PluginDocument.ScriptBlocks(page));
        Assert.Equal(["{}", "[1]"], PluginDocument.ScriptBlocks("""<script type="application/ld+json">{}</script><scripts><script src="a.js"></script><SCRIPT TYPE='application/json'>[1]</SCRIPT>"""));
    }

    [Fact]
    public async Task A_download_kept_under_another_version_of_the_spec_isn_t_matched_again_and_the_plugin_runs_at_once()
    {
        var spec = Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u" }""");
        var fetched = Assert.IsType<HoursFetch.Fetched>(await Source(spec, _ => Ok(Bytes("ultramar.json"))).FetchAsync(null, CancellationToken.None));
        var filtered = Source(Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u", "where": { "siteId": "1" } }"""), _ => throw new InvalidOperationException());
        var renamed = Source(Spec("""{ "name": "test-u", "label": "Ultra", "brand": "Ultramar", "url": "https://example.com/u" }"""), _ => throw new InvalidOperationException());

        Assert.Equal(DownloadKept.Current, Source(spec, _ => throw new InvalidOperationException()).Kept(fetched.Raw));
        Assert.Equal(DownloadKept.OtherSpec, filtered.Kept(fetched.Raw));
        Assert.Contains("another version of its spec", Assert.Throws<HoursFormatException>(() => filtered.Parse(fetched.Raw)).Message, StringComparison.Ordinal);

        // What's read again from the spec at each read (its label, brand, link) doesn't make the download another's.
        Assert.Equal(DownloadKept.Current, renamed.Kept(fetched.Raw));
        Assert.NotEmpty(renamed.Parse(fetched.Raw).Records);
    }

    [Fact]
    public void A_run_under_a_changed_spec_is_gated_as_a_new_source_s_and_a_takeover_or_a_new_engine_against_the_last_good_run()
    {
        var spec = Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u" }""");
        var source = Source(spec, _ => throw new InvalidOperationException());
        var narrowed = Source(Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u", "province": "ON" }"""), _ => throw new InvalidOperationException());
        var good = source.SpecFingerprint;

        Assert.Equal(40, HoursGate.Baseline(source, good, 40));
        Assert.Null(HoursGate.Baseline(narrowed, good, 40));

        // Whether its download is still on disk doesn't matter: the spec is kept with the last good run.
        Assert.NotEqual(good, narrowed.SpecFingerprint);

        // A plugin that took over a source in code: that source's last good run has no spec, and is the baseline.
        Assert.Equal(40, HoursGate.Baseline(source, null, 40));
        Assert.Equal(DownloadKept.OtherFormat, source.Kept(Bytes("ultramar.json")));

        // A new engine isn't a change of spec: the fingerprint doesn't hold it.
        Assert.DoesNotContain(HoursPluginSource.Engine.ToString(System.Globalization.CultureInfo.InvariantCulture), good!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_made_by_another_engine_isn_t_read_and_runs_again_with_the_same_spec()
    {
        var spec = Spec("""{ "name": "test-u", "label": "U", "url": "https://example.com/u" }""");
        var source = Source(spec, _ => throw new InvalidOperationException());
        var fetched = Assert.IsType<HoursFetch.Fetched>(await Source(spec, _ => Ok(Bytes("ultramar.json"))).FetchAsync(null, CancellationToken.None));
        var older = JsonNode.Parse(fetched.Raw)!;
        older["engine"] = HoursPluginSource.Engine - 1;

        Assert.Equal(DownloadKept.Current, source.Kept(fetched.Raw));
        Assert.Equal(DownloadKept.OtherEngine, source.Kept(Utf8(older)));
        Assert.Throws<HoursFormatException>(() => source.Parse(Utf8(older)));
    }

    [Fact]
    public void A_page_s_text_with_tags_never_closed_is_read_in_one_pass()
    {
        Assert.Equal("Lun-Ven\n 6h-22h ", PluginDocument.TextOf("Lun-Ven<br><b>6h-22h</b>"));
        Assert.Equal(200_001, PluginDocument.TextOf(new string('<', 200_000) + "x").Length);
    }

    [Fact]
    public void A_download_kept_by_another_source_isn_t_read_as_a_plugin_s()
    {
        var source = Source(Shipped("crevier"), _ => throw new InvalidOperationException());

        var error = Assert.Throws<HoursFormatException>(() => source.Parse(Bytes("ultramar.json")));

        Assert.Contains("isn't a plugin's records", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_is_turned_off_by_its_spec_or_by_the_settings_and_its_profile_is_its_spec_s()
    {
        var spec = Shipped("crevier");

        Assert.True(new HoursPluginSource(spec, Client(), NoPause(), Options()).Enabled);
        Assert.False(new HoursPluginSource(spec, Client(), NoPause(), Options(o => o.Plugins["Crevier"] = new HoursSourceOptions { Enabled = false })).Enabled);
        Assert.Equal(new Essence.Ranking.HoursSourceProfile("Crevier", Essence.Ranking.HoursSourceKind.Brand, 50), new HoursPluginSource(spec, Client(), NoPause(), Options()).Profile);
    }
}
