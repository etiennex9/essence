using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Essence.Hours;
using Essence.Ranking;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>A spec that can't be read: what's wrong, for the log.</summary>
public sealed class HoursPluginSpecException(string message) : Exception(message);

/// <summary>A filter on the records: only those whose field at <see cref="Path"/> is one of <see cref="Values"/>.</summary>
public sealed record PluginFilter(NodePath Path, IReadOnlyList<JsonNode?> Values)
{
    public bool Matches(JsonNode record) => Path.Select(record).Any(found => Values.Any(v => Same(found, v)));

    private static bool Same(JsonNode found, JsonNode? value) =>
        found is JsonValue a && value is JsonValue b && (a.GetValueKind(), b.GetValueKind()) switch
        {
            (JsonValueKind.String, JsonValueKind.String) => string.Equals(a.GetValue<string>().Trim(), b.GetValue<string>().Trim(), StringComparison.OrdinalIgnoreCase),
            (JsonValueKind.Number, JsonValueKind.Number) => a.GetValue<double>() == b.GetValue<double>(),
            (JsonValueKind.String, JsonValueKind.Number) or (JsonValueKind.Number, JsonValueKind.String) =>
                string.Equals(a.ToString().Trim(), b.ToString().Trim(), StringComparison.OrdinalIgnoreCase),
            var (x, y) => x == y && x is JsonValueKind.True or JsonValueKind.False,
        };
}

/// <summary>
/// An hours plugin: a source of stations' hours written as a spec (<c>Hours/Plugins/*.json</c> in the image, or
/// <c>Hours:PluginsPath</c>), read by the generic engine (<see cref="HoursPluginSource"/>). See <c>Hours/Plugins/README.md</c>.
/// </summary>
public sealed partial class HoursPluginSpec
{
    /// <summary>The <c>fields</c> a spec may give, each a <see cref="NodePath"/>, and their other spellings.</summary>
    private static readonly Dictionary<string, string> FieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["records"] = "records", ["id"] = "id", ["latitude"] = "latitude", ["lat"] = "latitude", ["longitude"] = "longitude", ["lon"] = "longitude",
        ["lng"] = "longitude", ["address"] = "address", ["postcode"] = "postcode", ["postalCode"] = "postcode", ["province"] = "province",
        ["country"] = "country", ["brand"] = "brand", ["link"] = "link", ["status"] = "status", ["hours"] = "hours", ["allDay"] = "allDay",
        ["allDayTags"] = "allDayTags", ["fuel"] = "fuel",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] Keys =
    [
        "name", "label", "kind", "rank", "brand", "link", "url", "params", "where", "format", "province", "matchRadiusM", "otherBrandReachM",
        "enabled", "fields", "hours", "note", "notes", "comment", "$schema",
    ];

    public required string Name { get; init; }
    public required string Label { get; init; }
    public HoursSourceKind Kind { get; init; } = HoursSourceKind.Brand;
    public int Rank { get; init; }

    /// <summary>The brand of a record that gives none (<see cref="BrandFamily.Of"/>).</summary>
    public string? Brand { get; init; }

    /// <summary>Where a person can check the hours, for a record without a page of its own.</summary>
    public string? Link { get; init; }

    /// <summary>The URL, with <c>{key}</c> where each of <see cref="Params"/>' values goes.</summary>
    public required string Url { get; init; }

    /// <summary>One request each, their records put together; none: one request.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Params { get; init; } = [];

    public IReadOnlyList<PluginFilter> Where { get; init; } = [];
    public PluginFormat Format { get; init; } = PluginFormat.Auto;

    /// <summary>The records kept: those in this province (<c>QC</c>), or that don't say where they are.</summary>
    public string? Province { get; init; } = "QC";

    public double MatchRadiusM { get; init; } = 150;
    public double? OtherBrandReachM { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Hints of where the records' fields are, by field: <c>id</c>, <c>hours</c>…</summary>
    public IReadOnlyDictionary<string, NodePath> Fields { get; init; } = new Dictionary<string, NodePath>();

    /// <summary>Ranges that mean the source doesn't say (<c>hours.unknown</c>): a record with one is left out.</summary>
    public IReadOnlyList<TimeRange> UnknownRanges { get; init; } = [];

    /// <summary>Where the spec comes from: <c>shipped crevier.json</c> or its path.</summary>
    public string Origin { get; init; } = "";

    /// <summary>The URLs asked, one per <see cref="Params"/>, each value escaped.</summary>
    public IReadOnlyList<Uri> Urls()
    {
        if (Params.Count == 0)
        {
            return [new Uri(Url)];
        }

        return [.. Params.Select(p => new Uri(p.Aggregate(Url, (url, param) => url.Replace($"{{{param.Key}}}", Uri.EscapeDataString(param.Value), StringComparison.Ordinal))))];
    }

    /// <summary>
    /// The name a spec that can't be read gives, for the log: its one <c>name</c>, a text, in JSON that's whole (a spec
    /// refused for what it says, or for a key twice). Null when it can't be told: JSON that breaks (a later name could
    /// follow), a name that isn't a text, or two. Never its file's name, which needn't be the spec's.
    /// </summary>
    public static string? NameOf(byte[] json)
    {
        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(PluginDocument.Decode(json)), new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("name"))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return null;
                    }

                    names.Add(reader.GetString()!.Trim());
                }
            }

            return names is { Count: 1 } && names.Single() is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // JSON that breaks, a text that isn't one (a lone surrogate), or anything else: the name can't be told.
            return null;
        }
    }

    /// <exception cref="HoursPluginSpecException">The spec isn't one: the message says why.</exception>
    public static HoursPluginSpec Parse(byte[] json, string origin)
    {
        try
        {
            return Read(json, origin);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // Whatever else a spec's JSON can throw is a spec that can't be read, never a reason to stop the ingest.
            throw new HoursPluginSpecException($"{origin} can't be read: {ex.Message}");
        }
    }

    private static HoursPluginSpec Read(byte[] json, string origin)
    {
        string text;
        try
        {
            // A spec is UTF-8: one that isn't would show its label garbled ("Ã‰nergies"), so it's refused, not guessed.
            text = StrictUtf8.GetString(json, json is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0, json.Length - (json is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            throw new HoursPluginSpecException($"{origin} isn't UTF-8: save it as UTF-8.");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: PluginDocument.Lenient) as JsonObject
                ?? throw new HoursPluginSpecException($"{origin} isn't a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new HoursPluginSpecException($"{origin} isn't valid JSON: {ex.Message}");
        }

        string Fail(string message) => throw new HoursPluginSpecException($"{origin}: {message}");

        if (root.Select(m => m.Key).FirstOrDefault(k => !Keys.Contains(k, StringComparer.Ordinal)) is { } unknownKey)
        {
            Fail($"\"{unknownKey}\" isn't a spec's field (see Hours/Plugins/README.md).");
        }

        // A field of the wrong kind refuses the spec: "matchRadiusM": "300" isn't let become the default.
        foreach (var (key, value) in root)
        {
            var valueKind = value?.GetValueKind() ?? JsonValueKind.Null;
            var expected = key switch
            {
                "name" or "label" or "kind" or "brand" or "link" or "url" or "format" => valueKind == JsonValueKind.String ? null : "a text",
                "province" => valueKind is JsonValueKind.String or JsonValueKind.Null ? null : "a text, or null for all",
                "rank" or "matchRadiusM" => valueKind == JsonValueKind.Number ? null : "a number",
                "otherBrandReachM" => valueKind is JsonValueKind.Number or JsonValueKind.Null ? null : "a number, or null",
                "enabled" => valueKind is JsonValueKind.True or JsonValueKind.False ? null : "true or false",
                "params" => valueKind == JsonValueKind.Array ? null : "a list",
                "where" or "fields" or "hours" => valueKind == JsonValueKind.Object ? null : "an object",
                _ => null,
            };
            if (expected is not null)
            {
                Fail($"\"{key}\" must be {expected}.");
            }
        }

        var name = Text(root, "name") ?? Fail("\"name\" is required.");
        if (!NamePattern().IsMatch(name))
        {
            Fail($"\"name\" \"{name}\" must be lowercase letters, digits and dashes, starting with a letter, 2 to 31 characters.");
        }

        var label = Text(root, "label") ?? Fail("\"label\" is required: how the app names the source.");
        if (label.Length > 60)
        {
            Fail("\"label\" must be at most 60 characters.");
        }

        var kind = HoursSourceKind.Brand;
        if (Text(root, "kind") is { } kindText && (!HoursSourceKinds.TryParse(kindText, out kind) || kind is not (HoursSourceKind.Brand or HoursSourceKind.OpenData)))
        {
            Fail($"\"kind\" must be \"brand\" or \"open-data\", not \"{kindText}\".");
        }

        var rank = kind == HoursSourceKind.Brand ? HoursSourceProfile.DefaultBrandRank : HoursSourceProfile.DefaultOpenDataRank;
        if (root["rank"] is { } rankNode && !(rankNode is JsonValue r && r.TryGetValue(out rank) && rank is >= 1 and <= 999))
        {
            Fail("\"rank\" must be a whole number from 1 to 999 (lower first; Costco's is 10, OpenStreetMap's 60).");
        }

        var url = Text(root, "url") ?? Fail("\"url\" is required.");
        var parameters = new List<IReadOnlyDictionary<string, string>>();
        if (root["params"] is { } paramsNode)
        {
            if (paramsNode is not JsonArray { Count: > 0 } list || list.Any(p => p is not JsonObject))
            {
                Fail("\"params\" must be a list of objects, one per request: [{\"lat\": 45.5, \"lon\": -73.6}].");
            }

            foreach (var item in ((JsonArray)paramsNode).Cast<JsonObject>())
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in item)
                {
                    values[key] = value is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
                        ? v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>().ToString("0.0#####", CultureInfo.InvariantCulture) : v.GetValue<string>()
                        : Fail($"\"params\"' \"{key}\" must be a text or a number.");
                }

                parameters.Add(values);
            }
        }

        var enabled = root["enabled"]?.GetValueKind() switch
        {
            null or JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Fail("\"enabled\" must be true or false.") is null,
        };

        var placeholders = PlaceholderPattern().Matches(url).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (!IsHttp(PlaceholderPattern().Replace(url, "0")))
        {
            Fail("\"url\" must be an absolute http(s) URL.");
        }

        if (parameters.Count == 0 && placeholders.Count > 0)
        {
            Fail($"\"url\" has {{{placeholders[0]}}} but there are no \"params\" to fill it.");
        }

        if (parameters.FirstOrDefault(p => placeholders.Any(k => !p.ContainsKey(k))) is { } missing)
        {
            Fail($"a \"params\" entry has no {{{placeholders.First(k => !missing.ContainsKey(k))}}} for the URL.");
        }

        var spec = new HoursPluginSpec
        {
            Name = name,
            Label = label,
            Kind = kind,
            Rank = rank,
            Brand = Text(root, "brand"),
            Link = Text(root, "link") is { } link && IsHttp(link) ? link : root["link"] is null ? null : Fail("\"link\" must be an absolute http(s) URL."),
            Url = url,
            Params = parameters,
            Where = Filters(root["where"], Fail),
            Format = Text(root, "format") switch
            {
                null or "auto" => PluginFormat.Auto,
                "json" => PluginFormat.Json,
                "csv" => PluginFormat.Csv,
                "html" => PluginFormat.Html,
                var other => throw new HoursPluginSpecException($"{origin}: \"format\" must be auto, json, csv or html, not \"{other}\"."),
            },
            Province = root.ContainsKey("province")
                ? root["province"] is null ? null : PluginMapper.ProvinceCode(Text(root, "province")) ?? Fail("\"province\" must be a Canadian province (QC), or null for all.")
                : "QC",
            MatchRadiusM = Number(root, "matchRadiusM") ?? 150,
            OtherBrandReachM = Number(root, "otherBrandReachM"),
            Enabled = enabled,
            Fields = Hints(root["fields"], Fail),
            UnknownRanges = Unknown(root["hours"], Fail),
            Origin = origin,
        };

        if (spec.MatchRadiusM is < 10 or > 2000)
        {
            Fail("\"matchRadiusM\" must be from 10 to 2000.");
        }

        if (spec.OtherBrandReachM is < 0 or > 500)
        {
            Fail("\"otherBrandReachM\" must be from 0 to 500, or left out.");
        }

        try
        {
            if (spec.Urls().Any(u => u.Scheme is not ("https" or "http")))
            {
                Fail("\"url\" must be an absolute http(s) URL.");
            }
        }
        catch (UriFormatException ex)
        {
            Fail($"\"url\" isn't a URL once filled in: {ex.Message}");
        }

        return spec;
    }

    private static IReadOnlyList<PluginFilter> Filters(JsonNode? node, Func<string, string> fail)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonObject where)
        {
            fail("\"where\" must be an object of paths and values: {\"hasGasDepartment\": true}.");
            return [];
        }

        var filters = new List<PluginFilter>();
        foreach (var (key, value) in where)
        {
            if (!NodePath.TryParse(key, out var path, out var error))
            {
                fail($"\"where\"' \"{key}\" isn't a path: {error}.");
            }

            IReadOnlyList<JsonNode?> values = value is JsonArray array ? [.. array.Select(v => v?.DeepClone())] : [value?.DeepClone()];
            if (values.Any(v => v is not JsonValue))
            {
                fail($"\"where\"' \"{key}\" must be a text, a number, true or false, or a list of them.");
            }

            filters.Add(new PluginFilter(path!, values));
        }

        return filters;
    }

    private static Dictionary<string, NodePath> Hints(JsonNode? node, Func<string, string> fail)
    {
        var hints = new Dictionary<string, NodePath>(StringComparer.Ordinal);
        if (node is null)
        {
            return hints;
        }

        if (node is not JsonObject fields)
        {
            fail("\"fields\" must be an object of paths: {\"id\": \"locationId\"}.");
            return hints;
        }

        foreach (var (key, value) in fields)
        {
            if (!FieldNames.TryGetValue(key, out var field))
            {
                fail($"\"fields\"' \"{key}\" isn't a field: {string.Join(", ", FieldNames.Values.Distinct())}.");
            }

            if (hints.ContainsKey(field!))
            {
                fail($"\"fields\" gives the {field} twice (\"{key}\" is another name of it): which is meant can't be told.");
            }

            var text = value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            if (!NodePath.TryParse(text, out var path, out var error))
            {
                fail($"\"fields\"' \"{key}\" isn't a path: {error}.");
            }

            hints[field!] = path!;
        }

        return hints;
    }

    private static List<TimeRange> Unknown(JsonNode? node, Func<string, string> fail)
    {
        var ranges = new List<TimeRange>();
        if (node is null)
        {
            return ranges;
        }

        if (node is not JsonObject hours || hours.Any(m => m.Key != "unknown") || hours["unknown"] is not JsonArray list)
        {
            fail("\"hours\" must be {\"unknown\": [\"00:00-12:00\"]}: the ranges that mean the source doesn't say.");
            return ranges;
        }

        foreach (var item in list)
        {
            var text = item is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
            if (text.Split('-', 2, StringSplitOptions.TrimEntries) is not [var open, var close] || !HoursText.TryRange(open, close, out var range))
            {
                fail($"\"hours\"' unknown range \"{text}\" isn't \"HH:MM-HH:MM\".");
                continue;
            }

            ranges.Add(range);
        }

        return ranges;
    }

    private static string? Text(JsonObject node, string field) =>
        node[field] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text ? text : null;

    private static double? Number(JsonObject node, string field) =>
        node[field] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    private static bool IsHttp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();
}
