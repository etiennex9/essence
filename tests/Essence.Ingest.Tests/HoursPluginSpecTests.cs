using System.Text;
using System.Text.Json.Nodes;
using Essence.Ingest.Hours;
using Essence.Ingest.Hours.Plugins;
using Essence.Ranking;

namespace Essence.Ingest.Tests;

/// <summary>The plugins' specs, the catalog of those shipped and of the server's folder, and the paths their hints give.</summary>
public sealed class HoursPluginSpecTests : IDisposable
{
    private static readonly HashSet<string> CodeSources = [CostcoSource.SourceName, UltramarSource.SourceName, ShellSource.SourceName];

    private readonly string _folder = Directory.CreateTempSubdirectory("essence-plugins-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static HoursPluginSpec Parse(string json) => HoursPluginSpec.Parse(Encoding.UTF8.GetBytes(json), "test");

    [Fact]
    public void The_specs_shipped_are_all_valid_and_none_takes_a_code_source_s_name()
    {
        var catalog = HoursPluginCatalog.Load(null, CodeSources);

        Assert.Empty(catalog.Problems);
        Assert.Equal(["crevier", "harnois"], catalog.Specs.Select(s => s.Name));
        Assert.All(catalog.Specs, s => Assert.StartsWith("shipped ", s.Origin, StringComparison.Ordinal));
    }

    [Fact]
    public void A_spec_s_defaults_a_brand_ranked_after_the_brands_in_code_in_Québec()
    {
        var spec = Parse("""
            // A comment is fine.
            { "name": "acme", "label": "Acme", "url": "https://example.com/stations", }
            """);

        Assert.Equal((HoursSourceKind.Brand, 50, "QC", 150.0, true, PluginFormat.Auto), (spec.Kind, spec.Rank, spec.Province, spec.MatchRadiusM, spec.Enabled, spec.Format));
        Assert.Equal([new Uri("https://example.com/stations")], spec.Urls());
        Assert.Equal(80, Parse("""{ "name": "ville", "label": "Ville", "kind": "open-data", "url": "https://example.com/s.csv" }""").Rank);
        Assert.Null(Parse("""{ "name": "all", "label": "All", "url": "https://example.com/s", "province": null }""").Province);
    }

    [Theory]
    [InlineData("""{ "label": "A", "url": "https://example.com" }""", "\"name\" is required")]
    [InlineData("""{ "name": "Acme", "label": "A", "url": "https://example.com" }""", "lowercase")]
    [InlineData("""{ "name": "acme", "url": "https://example.com" }""", "\"label\" is required")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "ftp://example.com" }""", "absolute http(s) URL")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "kind": "osm" }""", "\"kind\"")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "rank": 0 }""", "\"rank\"")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com?lat={lat}" }""", "no \"params\"")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com?lat={lat}", "params": [{ "lon": 1 }] }""", "has no {lat}")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "urls": [] }""", "\"urls\" isn't a spec's field")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "fields": { "phone": "tel" } }""", "\"phone\" isn't a field")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "fields": { "id": "a[" } }""", "isn't a path")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "hours": { "unknown": ["midi"] } }""", "HH:MM-HH:MM")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "province": "Atlantis" }""", "\"province\"")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "matchRadiusM": 5000 }""", "matchRadiusM")]
    [InlineData("not json", "isn't valid JSON")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "matchRadiusM": 100, "matchRadiusM": 200 }""", "isn't valid JSON")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "where": { "a": 1, "a": 2 } }""", "isn't valid JSON")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "fields": { "lat": "a", "latitude": "b" } }""", "gives the latitude twice")]
    [InlineData("""{ "name": "\uD800x", "label": "A", "url": "https://example.com" }""", "can't be read")]
    public void A_spec_that_isn_t_one_says_why(string json, string reason)
    {
        var error = Assert.Throws<HoursPluginSpecException>(() => Parse(json));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Params_fill_the_URL_each_value_escaped()
    {
        var spec = Parse("""{ "name": "acme", "label": "A", "url": "https://example.com/?q={q}&lat={lat}", "params": [{ "q": "a b", "lat": 45.5 }] }""");

        Assert.Equal("https://example.com/?q=a%20b&lat=45.5", spec.Urls().Single().AbsoluteUri);
    }

    [Fact]
    public void The_server_s_folder_adds_specs_replaces_a_shipped_one_and_one_broken_or_named_as_a_code_source_is_left_out_and_said()
    {
        File.WriteAllText(Path.Combine(_folder, "ville.json"), """{ "name": "ville", "label": "Ville", "kind": "open-data", "url": "https://example.com/s.csv" }""");
        File.WriteAllText(Path.Combine(_folder, "crevier.json"), """{ "name": "crevier", "label": "Crevier (test)", "url": "https://example.com/c", "enabled": false }""");
        File.WriteAllText(Path.Combine(_folder, "broken.json"), """{ "name": "broken" """);
        File.WriteAllText(Path.Combine(_folder, "costco.json"), """{ "name": "costco", "label": "Costco", "url": "https://example.com/c" }""");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a spec");

        var catalog = HoursPluginCatalog.Load(_folder, CodeSources);

        Assert.Equal(["crevier", "harnois", "ville"], catalog.Specs.Select(s => s.Name));
        Assert.Equal(("Crevier (test)", false), (catalog.Find("crevier")!.Label, catalog.Find("crevier")!.Enabled));
        Assert.Collection(
            catalog.Problems,
            p => Assert.Contains("broken.json isn't valid JSON", p, StringComparison.Ordinal),
            p => Assert.Contains("broken.json: no source's hours are removed on this start", p, StringComparison.Ordinal),
            p => Assert.Contains("\"costco\" is a source written in code", p, StringComparison.Ordinal));
        Assert.True(catalog.Incomplete);
        Assert.Empty(catalog.Unreadable);
    }

    [Fact]
    public void Every_server_s_spec_read_lets_the_sources_gone_be_removed()
    {
        File.WriteAllText(Path.Combine(_folder, "ville.json"), """{ "name": "ville", "label": "Ville", "kind": "open-data", "url": "https://example.com/s.csv" }""");

        var catalog = HoursPluginCatalog.Load(_folder, CodeSources);

        Assert.False(catalog.Incomplete);
        Assert.Equal(["crevier", "osm"], catalog.KeepRowsOf(["crevier", "osm"])!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_server_s_spec_that_can_t_be_read_is_said_never_stopping_the_start()
    {
        // A key twice used to throw past the catalog when the spec was read, stopping the whole ingest.
        File.WriteAllText(Path.Combine(_folder, "ville-a.json"), """{ "name": "ville", "label": "Ville", "url": "https://example.com/v", "matchRadiusM": 100, "matchRadiusM": 200 }""");
        File.WriteAllText(Path.Combine(_folder, "crevier.json"), """{ "name": "crevier", "label": "Crevier", "url": "https://example.com/c", "rank": "high" }""");

        var catalog = HoursPluginCatalog.Load(_folder, CodeSources);

        Assert.Equal(["crevier", "harnois"], catalog.Specs.Select(s => s.Name));
        Assert.Equal("shipped crevier.json", catalog.Find("crevier")!.Origin);
        Assert.Equal(4, catalog.Problems.Count);
        Assert.Equal(["crevier", "ville"], catalog.Unreadable.Order(StringComparer.Ordinal));
        Assert.True(catalog.Incomplete);
        Assert.Null(catalog.KeepRowsOf(["crevier", "harnois"]));
    }

    [Theory]
    [InlineData("""{ "name": 5, "label": "M", "url": "https://example.com/m" }""")]
    [InlineData("""{ "label": "M", "url": "https://example.com/m", "name": """)]
    [InlineData("""{ "name": "\uD800x", "label": "M", "url": "https://example.com/m" }""")]
    [InlineData("""{ "name": "mybrand", "label": "M", "name": "other", "url": "https://example.com/m" }""")]
    [InlineData("""{ "name": "other",, "name": "mybrand", "label": "M", "url": "https://example.com/m" }""")]
    [InlineData("""{ "name": "my_brand", "label": "M", "url": "https://example.com/m" }""")]
    public void A_server_s_spec_that_can_t_be_read_keeps_every_source_s_rows_whatever_its_name(string json)
    {
        // Its file's name needn't be the spec's (mybrand-v2.json for "mybrand"), and a spec broken may be one renamed:
        // whose rows are gone can't be told, so none are removed.
        File.WriteAllText(Path.Combine(_folder, "mybrand-v2.json"), json);

        var catalog = HoursPluginCatalog.Load(_folder, CodeSources);

        Assert.True(catalog.Incomplete);
        Assert.DoesNotContain("mybrand-v2", catalog.Unreadable);
        Assert.Null(catalog.KeepRowsOf(["crevier"]));
        Assert.Contains(catalog.Problems, p => p.Contains("no source's hours are removed on this start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "matchRadiusM": "300" }""", "\"matchRadiusM\" must be a number")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "kind": 5 }""", "\"kind\" must be a text")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "format": 5 }""", "\"format\" must be a text")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "brand": 5 }""", "\"brand\" must be a text")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "otherBrandReachM": "x" }""", "\"otherBrandReachM\" must be a number")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "enabled": "no" }""", "\"enabled\" must be true or false")]
    [InlineData("""{ "name": "acme", "label": "A", "url": "https://example.com", "fields": [] }""", "\"fields\" must be an object")]
    public void A_spec_s_field_of_the_wrong_kind_refuses_it_rather_than_taking_the_default(string json, string reason)
    {
        Assert.Contains(reason, Assert.Throws<HoursPluginSpecException>(() => Parse(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spec_that_isn_t_UTF_8_is_refused_not_shown_garbled()
    {
        var latin1 = Encoding.Latin1.GetBytes("""{ "name": "acme", "label": "Énergies", "url": "https://example.com" }""");

        Assert.Contains("isn't UTF-8", Assert.Throws<HoursPluginSpecException>(() => HoursPluginSpec.Parse(latin1, "test")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_isn_t_there_leaves_the_shipped_specs()
    {
        var catalog = HoursPluginCatalog.Load(Path.Combine(_folder, "missing"), CodeSources);

        Assert.Equal(2, catalog.Specs.Count);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void Paths_go_into_objects_arrays_filtered_items_and_alternatives()
    {
        var record = JsonNode.Parse("""
            {
              "location": {"coordinates": {"latitude": 45.5}},
              "services": [{"serviceType": "CStore", "hours": "a"}, {"serviceType": "Forecourt", "hours": "b"}],
              "tags": {"1": "24hrs", "2": "Diesel"}
            }
            """);

        Assert.Equal(45.5, NodePath.Parse("location.coordinates.latitude").First(record)!.GetValue<double>());
        Assert.Equal("b", NodePath.Parse("services[serviceType=forecourt].hours").First(record)!.GetValue<string>());
        Assert.Equal(["a", "b"], NodePath.Parse("services[].hours").Select(record).Select(n => n.GetValue<string>()));
        Assert.Equal(["24hrs", "Diesel"], NodePath.Parse("tags.*").Select(record).Select(n => n.GetValue<string>()));
        Assert.Equal("a", NodePath.Parse("missing | services[0].hours").First(record)!.GetValue<string>());
        Assert.False(NodePath.TryParse("a..b", out _, out _));
    }
}
