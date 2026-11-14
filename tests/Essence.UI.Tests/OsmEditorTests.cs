using System.Net;
using System.Text;
using System.Xml.Linq;
using Essence.UI.Services;

namespace Essence.UI.Tests;

/// <summary>
/// Sending a correction of a station's hours to OpenStreetMap's API 0.6, against canned answers: the element fetched, a
/// changeset opened, the element put back with only its hours and their check date changed, and the changeset closed,
/// whatever happens once it's open.
/// </summary>
public class OsmEditorTests
{
    private static readonly Uri Api = new("https://api.openstreetmap.org");
    private const string Token = "token-123";

    /// <summary>A node as the API gives it: who edited it last, its point, and tags with characters XML must escape.</summary>
    private const string Node = """
        <?xml version="1.0" encoding="UTF-8"?>
        <osm version="0.6" generator="openstreetmap-cgimap" copyright="OpenStreetMap and contributors">
          <node id="6756481766" visible="true" version="4" changeset="140000000" timestamp="2025-06-01T12:00:00Z" user="someone" uid="42" lat="45.5699123" lon="-73.7469456">
            <tag k="amenity" v="fuel"/>
            <tag k="brand" v="A&amp;W &lt;Gas&gt; &quot;Express&quot;"/>
            <tag k="opening_hours" v="Mo-Su 07:00-22:00"/>
            <tag k="name" v="Station d’essence"/>
          </node>
        </osm>
        """;

    private static readonly OsmHoursEdit Edit = new(
        new OsmElementRef("node", 6756481766), "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", new DateOnly(2026, 11, 13), "Essence 2026.11.13",
        "Update opening hours of A&W <Gas> \"Express\"", Surveyed: false);

    [Fact]
    public async Task The_whole_sequence_fetches_opens_puts_and_closes_as_the_user()
    {
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/0.6/node/6756481766") => OsmStub.Xml(Node),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => OsmStub.Text("5"),
            ("PUT", "/api/0.6/changeset/170000001/close") => OsmStub.Text(""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var changeset = await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        Assert.Equal(170000001, changeset);
        Assert.Equal(
            ["GET /api/0.6/node/6756481766", "PUT /api/0.6/changeset/create", "PUT /api/0.6/node/6756481766", "PUT /api/0.6/changeset/170000001/close"],
            osm.Requests.Select(r => $"{r.Method} {r.Path}"));
        Assert.All(osm.Requests, r => Assert.Equal($"Bearer {Token}", r.Authorization));
        Assert.All(osm.Requests.Where(r => r.Body.Length > 0), r => Assert.Equal("text/xml; charset=utf-8", r.ContentType));
    }

    [Fact]
    public async Task The_element_goes_back_with_only_its_hours_and_their_check_date_changed()
    {
        var osm = Sequence(Node);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        var put = osm.Requests[2];
        Assert.Equal(
            """<?xml version="1.0" encoding="utf-8"?><osm><node id="6756481766" version="4" changeset="170000001" lat="45.5699123" lon="-73.7469456">"""
            + """<tag k="amenity" v="fuel" /><tag k="brand" v="A&amp;W &lt;Gas&gt; &quot;Express&quot;" />"""
            + """<tag k="opening_hours" v="Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30" /><tag k="name" v="Station d’essence" />"""
            + """<tag k="check_date:opening_hours" v="2026-11-13" /></node></osm>""",
            put.Body);
        var tags = Tags(put.Body, "node");
        Assert.Equal("A&W <Gas> \"Express\"", tags["brand"]);
        Assert.Equal(["amenity", "brand", "opening_hours", "name", "check_date:opening_hours"], tags.Keys);
    }

    [Fact]
    public async Task The_changeset_says_who_made_it_and_why_its_comment_escaped_and_survey_only_when_checked_there()
    {
        var osm = Sequence(Node);
        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);
        var surveyed = Sequence(Node);
        await new OsmEditor(surveyed.Client).UpdateHoursAsync(Api, Token, Edit with { Surveyed = true }, TestContext.Current.CancellationToken);

        var create = osm.Requests[1].Body;
        Assert.Equal(
            """<?xml version="1.0" encoding="utf-8"?><osm><changeset><tag k="created_by" v="Essence 2026.11.13" />"""
            + """<tag k="comment" v="Update opening hours of A&amp;W &lt;Gas&gt; &quot;Express&quot;" /></changeset></osm>""",
            create);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["created_by"] = "Essence 2026.11.13",
                ["comment"] = "Update opening hours of A&W <Gas> \"Express\"",
                ["source"] = "survey",
            },
            Tags(surveyed.Requests[1].Body, "changeset"));
    }

    [Fact]
    public async Task An_element_without_hours_gets_them_and_their_check_date_at_the_end()
    {
        const string bare = """<osm><node id="6756481766" version="1" lat="45.5" lon="-73.7"><tag k="amenity" v="fuel"/></node></osm>""";
        var osm = Sequence(bare);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        Assert.Equal(["amenity", "opening_hours", "check_date:opening_hours"], Tags(osm.Requests[2].Body, "node").Keys);
    }

    [Fact]
    public async Task A_way_goes_back_with_its_nodes_in_their_order_and_no_point()
    {
        const string way = """
            <osm version="0.6"><way id="456" visible="true" version="7" changeset="1" user="someone" uid="2">
              <nd ref="11"/><nd ref="12"/><nd ref="13"/><nd ref="11"/>
              <tag k="amenity" v="fuel"/><tag k="check_date:opening_hours" v="2020-01-01"/>
            </way></osm>
            """;
        var osm = Sequence(way);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit with { Element = new OsmElementRef("way", 456) }, TestContext.Current.CancellationToken);

        var put = XDocument.Parse(osm.Requests[2].Body).Root!.Element("way")!;
        Assert.Equal("PUT /api/0.6/way/456", $"{osm.Requests[2].Method} {osm.Requests[2].Path}");
        Assert.Equal(["11", "12", "13", "11"], put.Elements("nd").Select(nd => (string)nd.Attribute("ref")!));
        Assert.Equal(("456", "7", "170000001"), ((string)put.Attribute("id")!, (string)put.Attribute("version")!, (string)put.Attribute("changeset")!));
        Assert.Null(put.Attribute("lat"));
        Assert.Null(put.Attribute("user"));
        Assert.Equal(["amenity", "check_date:opening_hours", "opening_hours"], Tags(osm.Requests[2].Body, "way").Keys);
        Assert.Equal("2026-11-13", Tags(osm.Requests[2].Body, "way")["check_date:opening_hours"]);
    }

    [Fact]
    public async Task A_version_conflict_fetches_the_newer_version_and_tries_once_more()
    {
        var newer = Node.Replace("version=\"4\"", "version=\"5\"").Replace("<tag k=\"amenity\" v=\"fuel\"/>", "<tag k=\"amenity\" v=\"fuel\"/><tag k=\"phone\" v=\"+1 450 555 0100\"/>");
        var fetches = 0;
        var puts = 0;
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(fetches++ == 0 ? Node : newer),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => puts++ == 0
                ? OsmStub.Text("Version mismatch: Provided 4, server had: 5 of Node 6756481766", HttpStatusCode.Conflict)
                : OsmStub.Text("6"),
            _ => OsmStub.Text(""),
        });

        var changeset = await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        Assert.Equal(170000001, changeset);
        Assert.Equal(
            ["GET", "PUT changeset/create", "PUT node", "GET", "PUT node", "PUT close"],
            osm.Requests.Select(r => r.Path.EndsWith("/close", StringComparison.Ordinal) ? "PUT close"
                : r.Path.EndsWith("/create", StringComparison.Ordinal) ? "PUT changeset/create"
                : r.Method == "PUT" ? "PUT node" : r.Method));
        var retried = XDocument.Parse(osm.Requests[4].Body).Root!.Element("node")!;
        Assert.Equal("5", (string)retried.Attribute("version")!);
        Assert.Equal("+1 450 555 0100", Tags(osm.Requests[4].Body, "node")["phone"]);
    }

    [Fact]
    public async Task A_second_conflict_fails_and_the_changeset_is_closed_all_the_same()
    {
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(Node),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => OsmStub.Text("Version mismatch", HttpStatusCode.Conflict),
            _ => OsmStub.Text(""),
        });

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.Equal(("Version mismatch", HttpStatusCode.Conflict), (error.Message, error.Status));
        Assert.Equal(2, osm.Requests.Count(r => r is { Method: "PUT", Path: "/api/0.6/node/6756481766" }));
        Assert.Equal("/api/0.6/changeset/170000001/close", osm.Requests[^1].Path);
    }

    [Fact]
    public async Task A_refused_edit_says_OpenStreetMap_s_reason_and_leaves_no_changeset_open()
    {
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(Node),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => OsmStub.Text("Element node/6756481766 has duplicate tags with key opening_hours", HttpStatusCode.BadRequest),
            ("PUT", _) => throw new HttpRequestException("The connection dropped."),
            _ => OsmStub.Text(""),
        });

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.Equal("Element node/6756481766 has duplicate tags with key opening_hours", error.Message);
        Assert.Equal("/api/0.6/changeset/170000001/close", osm.Requests[^1].Path);
    }

    [Fact]
    public async Task OpenStreetMap_unreachable_while_putting_still_closes_the_changeset()
    {
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(Node),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => throw new HttpRequestException("No route to host."),
            _ => OsmStub.Text(""),
        });

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal("/api/0.6/changeset/170000001/close", osm.Requests[^1].Path);
    }

    [Fact]
    public async Task A_changeset_that_couldn_t_be_opened_has_nothing_to_close()
    {
        var osm = new OsmStub(r => r.Method == HttpMethod.Get
            ? OsmStub.Xml(Node)
            : OsmStub.Text("Your access to the API has been blocked.", HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.Equal(["GET /api/0.6/node/6756481766", "PUT /api/0.6/changeset/create"], osm.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task A_deleted_element_is_said_so_before_anything_is_opened()
    {
        var osm = new OsmStub(_ => new HttpResponseMessage(HttpStatusCode.Gone));

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.Equal((OsmEditFailure.Deleted, HttpStatusCode.Gone), (error.Failure, error.Status));
        Assert.Single(osm.Requests);
    }

    [Theory]
    [InlineData("Mo-Fr 06:00-22:00; Sa,Su 07:00-21:00; Dec 25 off")]
    [InlineData("Mo-Su 06:00-22:00; SH off")]
    [InlineData("Mo-Su 06:00-22:00 \"pumps 24/7\"")]
    [InlineData("Mo-Fr 06:00-22:00 || \"by appointment\"")]
    [InlineData("sunrise-sunset")]
    public async Task Hours_on_OpenStreetMap_the_app_can_t_read_are_never_replaced_nothing_is_opened(string detailed)
    {
        var osm = Sequence(Node.Replace("Mo-Su 07:00-22:00", detailed.Replace("\"", "&quot;")));

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.Equal(OsmEditFailure.DetailedHours, error.Failure);
        Assert.Equal(["GET /api/0.6/node/6756481766"], osm.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Theory]
    [InlineData("mo-fr 6:00-21:30, Sa-Su 06:00-20:30")]
    [InlineData("  ")]
    public async Task Hours_the_app_reads_however_they_re_written_or_none_are_replaced(string readable)
    {
        var osm = Sequence(Node.Replace("Mo-Su 07:00-22:00", readable));

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        Assert.Equal(Edit.Rule, Tags(osm.Requests[2].Body, "node")["opening_hours"]);
    }

    [Fact]
    public async Task Detailed_hours_written_meanwhile_stop_the_retry_and_the_changeset_is_closed()
    {
        var newer = Node.Replace("version=\"4\"", "version=\"5\"").Replace("Mo-Su 07:00-22:00", "Mo-Su 07:00-22:00; Dec 25 off");
        var fetches = 0;
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(fetches++ == 0 ? Node : newer),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => OsmStub.Text("Version mismatch", HttpStatusCode.Conflict),
            _ => OsmStub.Text(""),
        });

        var error = await Assert.ThrowsAsync<OsmEditException>(
            () => new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken));

        Assert.Equal(OsmEditFailure.DetailedHours, error.Failure);
        Assert.Single(osm.Requests, r => r is { Method: "PUT", Path: "/api/0.6/node/6756481766" });
        Assert.Equal("/api/0.6/changeset/170000001/close", osm.Requests[^1].Path);
    }

    [Fact]
    public async Task Long_values_of_the_element_go_back_exactly_as_they_came_emoji_and_all()
    {
        // 127 emoji and "ab": 256 UTF-16 units, but 129 characters, as OpenStreetMap counts its 255.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F68C", 127)) + "ab";
        // 255 characters, the longest OpenStreetMap takes, one an emoji: 256 units.
        var longest = "\U0001F68C" + new string('a', 254);
        var osm = Sequence(Node.Replace("<tag k=\"amenity\" v=\"fuel\"/>",
            $"<tag k=\"amenity\" v=\"fuel\"/><tag k=\"description\" v=\"{emoji}\"/><tag k=\"note\" v=\"{longest}\"/>"));

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        var tags = Tags(osm.Requests[2].Body, "node");
        Assert.Equal(emoji, tags["description"]);
        Assert.Equal(longest, tags["note"]);
    }

    [Fact]
    public async Task A_long_comment_is_cut_at_OpenStreetMap_s_255_characters_never_through_one()
    {
        // "a" then emoji: cut by UTF-16 units at 254, it would end between an emoji's two halves.
        var name = "a" + string.Concat(Enumerable.Repeat("\U0001F68C", 300));
        var osm = Sequence(Node);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit with { Comment = name }, TestContext.Current.CancellationToken);

        var comment = Tags(osm.Requests[1].Body, "changeset")["comment"];
        Assert.Equal(255, comment.EnumerateRunes().Count());
        Assert.EndsWith("\U0001F68C…", comment);
        Assert.StartsWith(comment[..^1], name);
    }

    [Theory]
    [InlineData(254, 254)]
    [InlineData(255, 255)]
    [InlineData(256, 255)]
    public void Text_is_cut_in_characters_with_an_ellipsis_only_when_it_doesn_t_fit(int characters, int kept)
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F68C", characters));

        var cut = OsmEditor.Cut(text, 255);

        Assert.Equal(kept, cut.EnumerateRunes().Count());
        Assert.Equal(characters > 255, cut.EndsWith('…'));
    }

    [Fact]
    public async Task What_stopped_an_edit_is_told_apart_for_the_editor_to_say_it()
    {
        static async Task<OsmEditFailure> FailureOf(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            (await Assert.ThrowsAsync<OsmEditException>(() => new OsmEditor(new OsmStub(respond).Client)
                .UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken))).Failure;

        Assert.Equal(OsmEditFailure.NotLinked, await FailureOf(_ => OsmStub.Text("Couldn't authenticate you", HttpStatusCode.Unauthorized)));
        Assert.Equal(OsmEditFailure.Refused, await FailureOf(_ => OsmStub.Text("Your access to the API has been blocked.", HttpStatusCode.Forbidden)));
        Assert.Equal(OsmEditFailure.Unreachable, await FailureOf(_ => throw new HttpRequestException("No route to host.")));
        Assert.Equal(OsmEditFailure.BadAnswer, await FailureOf(_ => OsmStub.Xml("<html>")));
        Assert.Equal(OsmEditFailure.BadAnswer, await FailureOf(_ => OsmStub.Xml(Node.Replace("6756481766", "1"))));
    }

    [Fact]
    public async Task A_relation_is_never_edited()
    {
        var osm = new OsmStub(_ => throw new InvalidOperationException("Nothing should be asked."));

        var error = await Assert.ThrowsAsync<OsmEditException>(() => new OsmEditor(osm.Client).UpdateHoursAsync(
            Api, Token, Edit with { Element = new OsmElementRef("relation", 789) }, TestContext.Current.CancellationToken));

        Assert.Equal(OsmEditFailure.NotEditable, error.Failure);
        Assert.Empty(osm.Requests);
    }

    /// <summary>
    /// An element of another brand: its brand, its Wikidata and Wikipedia, a name that's the brand's (in capitals), hours,
    /// and tags of its own.
    /// </summary>
    private const string OtherBrand = """
        <osm version="0.6">
          <node id="6756481766" visible="true" version="9" changeset="1" user="someone" uid="2" lat="48.42" lon="-71.07">
            <tag k="amenity" v="fuel"/>
            <tag k="brand" v="Sonic"/>
            <tag k="brand:wikidata" v="Q1234567"/>
            <tag k="brand:wikipedia" v="fr:Example"/>
            <tag k="name" v="SONIC"/>
            <tag k="opening_hours" v="Mo-Su 07:00-22:00"/>
            <tag k="operator" v="Example Inc."/>
            <tag k="shop" v="convenience"/>
          </node>
        </osm>
        """;

    private static readonly OsmHoursEdit BrandEdit = Edit with
    {
        Comment = "Update opening hours and brand of Nutrinor Énergies",
        Brand = "Nutrinor Énergies",
    };

    [Fact]
    public async Task With_the_brand_set_only_the_hours_the_brand_its_Wikidata_and_a_name_that_was_the_old_brand_change()
    {
        var osm = Sequence(OtherBrand);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, BrandEdit, TestContext.Current.CancellationToken);

        Assert.Equal(
            """<?xml version="1.0" encoding="utf-8"?><osm><node id="6756481766" version="9" changeset="170000001" lat="48.42" lon="-71.07">"""
            + """<tag k="amenity" v="fuel" /><tag k="brand" v="Nutrinor Énergies" /><tag k="name" v="Nutrinor Énergies" />"""
            + """<tag k="opening_hours" v="Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30" /><tag k="operator" v="Example Inc." />"""
            + """<tag k="shop" v="convenience" /><tag k="check_date:opening_hours" v="2026-11-13" /></node></osm>""",
            osm.Requests[2].Body);
    }

    [Fact]
    public async Task Without_the_brand_set_the_brand_its_Wikidata_and_the_name_stay_as_they_were()
    {
        var osm = Sequence(OtherBrand);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, Edit, TestContext.Current.CancellationToken);

        var before = Tags(OtherBrand, "node");
        var after = Tags(osm.Requests[2].Body, "node");
        Assert.Equal(before.Keys.Append(OsmEditor.CheckDateTag), after.Keys);
        Assert.Equal(
            before.Where(t => t.Key != OsmEditor.HoursTag),
            after.Where(t => t.Key is not (OsmEditor.HoursTag or OsmEditor.CheckDateTag)));
    }

    [Fact]
    public async Task An_element_without_a_brand_gets_it_at_the_end_and_keeps_a_name_of_its_own()
    {
        const string unbranded = """
            <osm><way id="456" version="3"><nd ref="11"/><nd ref="12"/><nd ref="11"/>
              <tag k="amenity" v="fuel"/><tag k="name" v="Dépanneur Sonic"/>
            </way></osm>
            """;
        var osm = Sequence(unbranded);

        await new OsmEditor(osm.Client).UpdateHoursAsync(
            Api, Token, BrandEdit with { Element = new OsmElementRef("way", 456) }, TestContext.Current.CancellationToken);

        var tags = Tags(osm.Requests[2].Body, "way");
        Assert.Equal(["amenity", "name", "opening_hours", "check_date:opening_hours", "brand"], tags.Keys);
        Assert.Equal(("Dépanneur Sonic", "Nutrinor Énergies"), (tags["name"], tags["brand"]));
    }

    [Theory]
    [InlineData("Sonic", "Sonic", "Nutrinor Énergies")]
    [InlineData("SONIC", "Sonic", "Nutrinor Énergies")]
    [InlineData("Pétro T", "Petro-T", "Nutrinor Énergies")]
    [InlineData("Dépanneur Sonic", "Sonic", "Dépanneur Sonic")]
    [InlineData("Station Nutrinor Énergies Chicoutimi", "Sonic", "Station Nutrinor Énergies Chicoutimi")]
    [InlineData("Sonic", null, "Sonic")]
    public void The_name_becomes_the_brand_only_when_it_was_the_old_brand_s(string name, string? oldBrand, string expected)
    {
        var tags = new Dictionary<string, string> { ["amenity"] = "fuel", ["name"] = name };
        if (oldBrand is not null)
        {
            tags["brand"] = oldBrand;
        }

        var changes = OsmEditor.BrandChanges(tags, "Nutrinor Énergies");

        Assert.Equal("Nutrinor Énergies", changes["brand"]);
        Assert.Equal(expected, changes.TryGetValue("name", out var changed) ? changed : name);
        Assert.Equal(expected != name, changes.ContainsKey("name"));
    }

    [Fact]
    public void An_element_without_a_name_gets_none()
    {
        var changes = OsmEditor.BrandChanges(new Dictionary<string, string> { ["amenity"] = "fuel", ["brand"] = "Sonic" }, "Nutrinor Énergies");

        Assert.Equal(new Dictionary<string, string?> { ["brand"] = "Nutrinor Énergies" }, changes);
    }

    [Theory]
    [InlineData("Nutrinor Énergies")]
    [InlineData("NUTRINOR ENERGIES")]
    [InlineData("Nutrinor-Energies")]
    public void An_element_already_of_the_station_s_brand_keeps_its_spelling_and_its_Wikidata(string onOsm)
    {
        var tags = new Dictionary<string, string> { ["brand"] = onOsm, ["brand:wikidata"] = "Q1234567", ["name"] = onOsm };

        Assert.Empty(OsmEditor.BrandChanges(tags, " Nutrinor Énergies "));
        Assert.Empty(OsmEditor.BrandChanges(tags, ""));
    }

    [Fact]
    public async Task A_version_conflict_sets_the_brand_again_on_the_newer_version_as_it_is()
    {
        // Renamed meanwhile, to a name of its own, and given a phone number: those stay, the brand is still set.
        var newer = OtherBrand.Replace("version=\"9\"", "version=\"10\"").Replace("v=\"SONIC\"", "v=\"Dépanneur du Coin\"")
            .Replace("<tag k=\"amenity\" v=\"fuel\"/>", "<tag k=\"amenity\" v=\"fuel\"/><tag k=\"phone\" v=\"+1 418 555 0100\"/>");
        var fetches = 0;
        var puts = 0;
        var osm = new OsmStub(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(fetches++ == 0 ? OtherBrand : newer),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", "/api/0.6/node/6756481766") => puts++ == 0
                ? OsmStub.Text("Version mismatch: Provided 9, server had: 10 of Node 6756481766", HttpStatusCode.Conflict)
                : OsmStub.Text("11"),
            _ => OsmStub.Text(""),
        });

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, BrandEdit, TestContext.Current.CancellationToken);

        var first = Tags(osm.Requests[2].Body, "node");
        var retried = Tags(osm.Requests[4].Body, "node");
        Assert.Equal("Nutrinor Énergies", first["name"]);
        Assert.Equal("10", (string)XDocument.Parse(osm.Requests[4].Body).Root!.Element("node")!.Attribute("version")!);
        Assert.Equal(
            ["amenity", "phone", "brand", "name", "opening_hours", "operator", "shop", "check_date:opening_hours"],
            retried.Keys);
        Assert.Equal(
            ("Nutrinor Énergies", "Dépanneur du Coin", "+1 418 555 0100", BrandEdit.Rule),
            (retried["brand"], retried["name"], retried["phone"], retried["opening_hours"]));
    }

    [Fact]
    public async Task The_changeset_s_comment_says_the_brand_was_set_too()
    {
        var osm = Sequence(OtherBrand);

        await new OsmEditor(osm.Client).UpdateHoursAsync(Api, Token, BrandEdit, TestContext.Current.CancellationToken);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["created_by"] = "Essence 2026.11.13",
                ["comment"] = "Update opening hours and brand of Nutrinor Énergies",
            },
            Tags(osm.Requests[1].Body, "changeset"));
    }

    [Fact]
    public async Task The_element_s_tags_are_read_without_a_token_from_the_API_named()
    {
        var osm = new OsmStub(r => r.RequestUri!.AbsolutePath == "/api/0.6/way/456"
            ? OsmStub.Xml("""<osm><way id="456" version="3"><nd ref="1"/><tag k="amenity" v="fuel"/><tag k="name" v="Dépanneur Sonic"/></way></osm>""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var tags = await new OsmEditor(osm.Client).ReadTagsAsync(
            new Uri("https://master.apis.dev.openstreetmap.org/"), new OsmElementRef("way", 456), TestContext.Current.CancellationToken);

        var read = Assert.Single(osm.Requests);
        Assert.Equal(("GET", "https://master.apis.dev.openstreetmap.org/api/0.6/way/456", (string?)null), (read.Method, read.Url.AbsoluteUri, read.Authorization));
        Assert.Equal(("Dépanneur Sonic", (string?)null, "Dépanneur Sonic"), (tags!.Name, tags.Brand, tags.Label));
    }

    [Fact]
    public async Task Tags_that_can_t_be_read_are_none_and_say_nothing()
    {
        static async Task<OsmElementTags?> Read(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            await new OsmEditor(new OsmStub(respond).Client).ReadTagsAsync(Api, Edit.Element, TestContext.Current.CancellationToken);

        Assert.Null(await Read(_ => new HttpResponseMessage(HttpStatusCode.Gone)));
        Assert.Null(await Read(_ => throw new HttpRequestException("No route to host.")));
        Assert.Null(await Read(_ => OsmStub.Xml("<html>")));
        Assert.Null(await Read(_ => OsmStub.Xml(Node.Replace("6756481766", "1"))));
        Assert.NotNull(await Read(_ => OsmStub.Xml(Node)));
    }

    [Theory]
    [InlineData("Dépanneur Sonic", "Sonic", "Dépanneur Sonic")]
    [InlineData(null, "Sonic", "Sonic")]
    [InlineData("  ", "Sonic", "Sonic")]
    [InlineData(null, null, null)]
    public void An_element_is_named_by_its_name_else_its_brand(string? name, string? brand, string? label)
    {
        var tags = new Dictionary<string, string> { ["amenity"] = "fuel" };
        if (name is not null)
        {
            tags["name"] = name;
        }

        if (brand is not null)
        {
            tags["brand"] = brand;
        }

        Assert.Equal(label, new OsmElementTags(tags).Label);
    }

    [Theory]
    [InlineData("node/123", "node", 123L)]
    [InlineData("way/456", "way", 456L)]
    [InlineData("relation/789", "relation", 789L)]
    public void An_element_reads_as_OpenStreetMap_names_it(string text, string type, long id)
    {
        Assert.True(OsmElementRef.TryParse(text, out var element));
        Assert.Equal(new OsmElementRef(type, id), element);
        Assert.Equal(text, element.ToString());
        Assert.Equal(type != "relation", element.IsEditable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("node/")]
    [InlineData("node/-1")]
    [InlineData("node/0")]
    [InlineData("area/123")]
    [InlineData("node/12/34")]
    [InlineData("6000140")]
    public void Anything_else_isn_t_an_element(string? text) => Assert.False(OsmElementRef.TryParse(text, out _));

    /// <summary>The element, then a changeset 170000001, then the edit and the close, all accepted.</summary>
    private static OsmStub Sequence(string element) =>
        new(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("GET", _) => OsmStub.Xml(element),
            ("PUT", "/api/0.6/changeset/create") => OsmStub.Text("170000001"),
            ("PUT", var p) when p.EndsWith("/close", StringComparison.Ordinal) => OsmStub.Text(""),
            ("PUT", _) => OsmStub.Text("2"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

    private static Dictionary<string, string> Tags(string xml, string element) =>
        XDocument.Parse(xml).Root!.Element(element)!.Elements("tag").ToDictionary(t => (string)t.Attribute("k")!, t => (string)t.Attribute("v")!);
}

/// <summary>Canned answers from OpenStreetMap, recording each request with its body (read before the request is disposed of).</summary>
internal sealed class OsmStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Request(string Method, string Path, string? Authorization, string? ContentType, string Body, Uri Url);

    public List<Request> Requests { get; } = [];

    public HttpClient Client => new(this, disposeHandler: false);

    public static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Request(
            request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.ToString(), body, request.RequestUri));
        return respond(request);
    }
}
