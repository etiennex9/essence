using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Essence.Data;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>Where a part of a record (the place itself, or its forecourt) gives its hours, its 24-hour flag, its status and its brand.</summary>
/// <param name="Hours">The fields of its hours, those of the fuel's (<see cref="FuelHours"/>) first.</param>
/// <param name="FuelHours">
/// How many of the first <see cref="Hours"/> are the fuel's, or given by the spec: the first of those that gives hours is
/// read. The others are alternatives (schema.org's <c>openingHours</c> and <c>openingHoursSpecification</c>), which must agree.
/// </param>
public sealed record PartMapping(IReadOnlyList<NodePath> Hours, NodePath? AllDay, NodePath? Status, NodePath? ClosedFlag, NodePath? Brand, int FuelHours = 0)
{
    /// <summary>Its special hours' fields: each entry must be dated (a holiday's, a season's), or the week can't be told.</summary>
    public IReadOnlyList<NodePath> Exceptions { get; init; } = [];

    public static readonly PartMapping Empty = new([], null, null, null, null);

    public bool IsEmpty => Hours.Count == 0 && AllDay is null && Status is null && ClosedFlag is null && Brand is null;
}

/// <summary>The fuel's part of each record (<c>services[serviceType=Forecourt]</c>), whose hours and status come first.</summary>
public sealed record FuelMapping(NodePath Items, PartMapping Part);

/// <summary>
/// Where a plugin's records are in its answer, and where each of their fields is: found by <see cref="PluginMapper"/> from
/// the fields' names and values, or given by the spec's hints. Paths are the records' own.
/// </summary>
/// <param name="Records">The records' path in the answer; empty when the answer is the list itself.</param>
/// <param name="Point">A GeoJSON point's <c>coordinates</c> ([longitude, latitude]), when there's no latitude and longitude.</param>
/// <param name="Swapped">The fields named latitude and longitude hold each other's values.</param>
/// <param name="PostcodeInAddress">The postal code is read from the address, having no field of its own.</param>
/// <param name="AllDayTags">A list of services or tags of which one says "24h": the place is open 24 hours.</param>
public sealed record PluginMapping(
    string Records,
    NodePath? Id,
    NodePath? Latitude,
    NodePath? Longitude,
    NodePath? Point,
    bool Swapped,
    NodePath? Address,
    NodePath? Postcode,
    bool PostcodeInAddress,
    NodePath? Province,
    NodePath? Country,
    NodePath? Brand,
    NodePath? Link,
    PartMapping Site,
    NodePath? AllDayTags,
    FuelMapping? Fuel)
{
    /// <summary>Other fields holding a country: a record any of them says isn't in Canada is left out.</summary>
    public IReadOnlyList<NodePath> OtherCountries { get; init; } = [];

    /// <summary>The mapping of an answer whose records the spec's filters all left out.</summary>
    public static PluginMapping Empty(string records) =>
        new(records, null, null, null, null, false, null, null, false, null, null, null, null, PartMapping.Empty, null, null);

    /// <summary>The mapping as logged and kept on disk with the records: each field and where it was found.</summary>
    public JsonObject Describe()
    {
        var description = new JsonObject
        {
            ["records"] = Records.Length == 0 ? "(the answer itself)" : Records,
            ["id"] = Id?.Text ?? "(the record's key, or its point)",
            ["point"] = Point is not null ? $"{Point.Text} (GeoJSON)" : $"{Latitude?.Text}, {Longitude?.Text}{(Swapped ? " (swapped)" : "")}",
        };
        Add(description, "address", Address);
        if (PostcodeInAddress)
        {
            description["postcode"] = "(in the address)";
        }
        else
        {
            Add(description, "postcode", Postcode);
        }

        Add(description, "province", Province);
        Add(description, "country", Country);
        if (OtherCountries.Count > 0)
        {
            description["otherCountries"] = string.Join(", ", OtherCountries.Select(c => c.Text));
        }

        Add(description, "brand", Brand);
        Add(description, "link", Link);
        Describe(description, "", Site);
        Add(description, "allDayTags", AllDayTags);
        if (Fuel is not null)
        {
            description["fuel"] = Fuel.Items.Text;
            Describe(description, "fuel.", Fuel.Part);
        }

        return description;

        static void Describe(JsonObject description, string prefix, PartMapping part)
        {
            if (part.Hours.Count > 0)
            {
                description[prefix + "hours"] = string.Join(" | ", part.Hours.Select((h, i) => i < part.FuelHours ? $"{h.Text} (first)" : h.Text));
            }

            Add(description, prefix + "allDay", part.AllDay);
            Add(description, prefix + "status", part.Status);
            Add(description, prefix + "closedFlag", part.ClosedFlag);
            Add(description, prefix + "brand", part.Brand);
        }

        static void Add(JsonObject description, string name, NodePath? path)
        {
            if (path is not null)
            {
                description[name] = path.Text;
            }
        }
    }
}

/// <summary>
/// Finds a plugin's records in its answer and where their fields are, by rules, never by guessing a value: the list of
/// objects (or object of objects, keyed by id) where most items have a point; then each field from its name in French or
/// English and its values across the records (an id unique to each, a point in range, a postal code shaped as one, a
/// province that is one, a status in the words it knows). The spec's hints (<see cref="HoursPluginSpec.Fields"/>) win
/// over what's found.
/// </summary>
public static partial class PluginMapper
{
    /// <summary>Records looked at to find where their fields are.</summary>
    private const int Sample = 500;

    private const int MaxDepth = 3;

    private static readonly string[] FuelWords =
        ["forecourt", "fuel", "fuels", "gas", "gasoline", "gasstation", "gasbar", "carburant", "carburants", "essence", "posteessence", "posteadessence", "pumps", "pompes", "petrol"];

    private static readonly string[] FuelNameParts = ["fuel", "gas", "forecourt", "carburant", "essence", "pump", "pompe"];

    private static readonly string[] TypeKeys = ["type", "servicetype", "category", "kind", "name", "label", "departmenttype", "departmentname", "servicename", "service"];

    private static readonly string[] HoursNameParts = ["hour", "heure", "horaire", "schedule", "opening", "periods", "weekdaytext"];

    private static readonly string[] NotHoursNameParts = ["special", "holiday", "ferie", "exception", "temporary", "upcoming", "note", "comment", "message"];

    /// <summary>The province codes by their names and abbreviations, folded and without spaces or dashes.</summary>
    private static readonly Dictionary<string, string> Provinces = new(StringComparer.Ordinal)
    {
        ["qc"] = "QC", ["quebec"] = "QC", ["que"] = "QC", ["pq"] = "QC",
        ["on"] = "ON", ["ontario"] = "ON", ["ont"] = "ON",
        ["nb"] = "NB", ["newbrunswick"] = "NB", ["nouveaubrunswick"] = "NB",
        ["ns"] = "NS", ["novascotia"] = "NS", ["nouvelleecosse"] = "NS",
        ["pe"] = "PE", ["pei"] = "PE", ["princeedwardisland"] = "PE", ["ileduprinceedouard"] = "PE",
        ["nl"] = "NL", ["newfoundlandandlabrador"] = "NL", ["terreneuveetlabrador"] = "NL", ["newfoundland"] = "NL",
        ["mb"] = "MB", ["manitoba"] = "MB",
        ["sk"] = "SK", ["saskatchewan"] = "SK",
        ["ab"] = "AB", ["alberta"] = "AB",
        ["bc"] = "BC", ["britishcolumbia"] = "BC", ["colombiebritannique"] = "BC",
        ["yt"] = "YT", ["yukon"] = "YT",
        ["nt"] = "NT", ["northwestterritories"] = "NT", ["territoiresdunordouest"] = "NT",
        ["nu"] = "NU", ["nunavut"] = "NU",
    };

    private static readonly Dictionary<string, string> Countries = new(StringComparer.Ordinal)
    {
        ["ca"] = "CA", ["can"] = "CA", ["canada"] = "CA",
        ["us"] = "US", ["usa"] = "US", ["unitedstates"] = "US", ["unitedstatesofamerica"] = "US", ["etatsunis"] = "US",
    };

    /// <summary>The province a Canadian postal code is in, by its first letter.</summary>
    private static readonly Dictionary<char, string> PostcodeProvinces = new()
    {
        ['A'] = "NL", ['B'] = "NS", ['C'] = "PE", ['E'] = "NB", ['G'] = "QC", ['H'] = "QC", ['J'] = "QC", ['K'] = "ON", ['L'] = "ON",
        ['M'] = "ON", ['N'] = "ON", ['P'] = "ON", ['R'] = "MB", ['S'] = "SK", ['T'] = "AB", ['V'] = "BC", ['Y'] = "YT",
    };

    public static string? ProvinceCode(string? text) => text is null ? null : Provinces.GetValueOrDefault(Squeeze(text));

    public static string? CountryCode(string? text) => text is null ? null : Countries.GetValueOrDefault(Squeeze(text));

    public static string? ProvinceOfPostcode(string? postcode) =>
        StationKey.NormalizePostalCode(postcode) is [var first, ..] && PostcodePattern().IsMatch(postcode!.Trim()) ? PostcodeProvinces.GetValueOrDefault(first) : null;

    /// <summary>A Canadian postal code somewhere in the text ("…, Laval QC H7P 5P4"), or null.</summary>
    public static string? PostcodeIn(string? text) => text is null ? null : PostcodeSearch().Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>A service or tag that says the place is open 24 hours: "24h", "24hrs", "24 heures", "Ouvert 24h", "24/7", "Open 24 hours".</summary>
    public static bool IsAllDayTag(string? text) => text is not null && AllDayTagPattern().IsMatch(StationKey.Fold(text).Trim());

    /// <summary>The records of an answer, by the hint or as found, each with its key in an object keyed by id.</summary>
    /// <exception cref="HoursFormatException">The hint gives nothing, or no list of records with points is found.</exception>
    public static (string Path, IReadOnlyList<(string? Key, JsonObject Item)> Items) Records(JsonNode root, NodePath? hint, string what)
    {
        if (hint is not null)
        {
            var found = hint.Select(root);
            var items = new List<(string?, JsonObject)>();
            foreach (var node in found)
            {
                switch (node)
                {
                    case JsonArray array:
                        items.AddRange(array.OfType<JsonObject>().Select(o => ((string?)null, o)));
                        break;
                    case JsonObject obj when IsKeyed(obj):
                        items.AddRange(obj.Where(m => m.Value is JsonObject).Select(m => ((string?)m.Key, (JsonObject)m.Value!)));
                        break;
                    case JsonObject obj:
                        items.Add((null, obj));
                        break;
                }
            }

            return items.Count > 0 ? (hint.Text, items) : throw new HoursFormatException($"{what} has no records at {hint.Text}.");
        }

        var candidates = new Dictionary<string, List<(string?, JsonObject)>>(StringComparer.Ordinal);
        Walk(root, "", 0);
        var best = candidates
            .Select(c => (Path: c.Key, Items: c.Value, Score: c.Value.Count(i => HasPoint(i.Item2))))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Path.Count(ch => ch is '.' or '['))
            .FirstOrDefault();
        return best.Items is { } chosen ? (best.Path, chosen) : throw new HoursFormatException($"{what} has no list of places with a latitude and longitude.");

        void Walk(JsonNode? node, string path, int depth)
        {
            if (depth > 6)
            {
                return;
            }

            switch (node)
            {
                case JsonArray array:
                    var objects = array.OfType<JsonObject>().ToList();
                    if (objects.Count > 0 && objects.Count * 2 >= array.Count)
                    {
                        Add(path, objects.Select(o => ((string?)null, o)));
                        foreach (var obj in objects.Take(Sample))
                        {
                            WalkMembers(obj, path + "[]", depth);
                        }
                    }

                    break;
                case JsonObject obj when IsKeyed(obj):
                    Add(path, obj.Where(m => m.Value is JsonObject).Select(m => ((string?)m.Key, (JsonObject)m.Value!)));

                    // A few objects are as likely the answer's parts ({"stores": {…}, "form": {…}}) as records: by name too.
                    if (obj.Count <= 20)
                    {
                        WalkMembers(obj, path, depth);
                    }

                    foreach (var value in obj.Select(m => m.Value).OfType<JsonObject>().Take(Sample))
                    {
                        WalkMembers(value, path.Length == 0 ? "*" : path + ".*", depth);
                    }

                    break;
                case JsonObject obj:
                    WalkMembers(obj, path, depth);
                    break;
            }
        }

        void WalkMembers(JsonObject obj, string path, int depth)
        {
            foreach (var (key, value) in obj)
            {
                if (value is JsonArray or JsonObject && IsPathable(key))
                {
                    Walk(value, path.Length == 0 ? key : $"{path}.{key}", depth + 1);
                }
            }
        }

        void Add(string path, IEnumerable<(string?, JsonObject)> items)
        {
            if (!candidates.TryGetValue(path, out var list))
            {
                candidates[path] = list = [];
            }

            list.AddRange(items);
        }
    }

    /// <summary>An answer that says more pages follow (<c>hasNextPage</c>, <c>hasMore</c>): its records aren't all there.</summary>
    public static bool HasMorePages(JsonNode root)
    {
        return Check(root, 0);

        static bool Check(JsonNode? node, int depth) => node is JsonObject obj && depth <= 2 && obj.Any(m =>
            (GenericHoursReader.Normalize(m.Key) is "hasnextpage" or "hasmore" or "hasnext" && m.Value?.GetValueKind() == JsonValueKind.True)
            || (m.Value is JsonObject && Check(m.Value, depth + 1)));
    }

    /// <summary>
    /// An answer that says it failed (<c>"success": false</c>, <c>"ok": "false"</c>, <c>"success": 0</c>), or doesn't say
    /// it succeeded in a way that's understood: what it holds may be partial or old.
    /// </summary>
    public static bool SaysFailed(JsonNode root) =>
        root is JsonObject obj && obj.Any(m => GenericHoursReader.Normalize(m.Key) is "success" or "ok" or "succeeded" && !SaysYes(m.Value));

    private static bool SaysYes(JsonNode? value) => value switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => GenericHoursReader.Normalize(v.GetValue<string>()) is "true" or "yes" or "ok" or "1" or "success",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>() == 1,
        _ => false,
    };

    /// <summary>Where the records' fields are: what the hints say, the rest as found.</summary>
    public static PluginMapping Map(string recordsPath, IReadOnlyList<(string? Key, JsonObject Item)> records, IReadOnlyDictionary<string, NodePath> hints)
    {
        var items = records.Take(Sample).Select(r => r.Item).ToList();
        var columns = Columns(items);

        var (latitude, longitude, point, swapped) = hints.ContainsKey("latitude") || hints.ContainsKey("longitude")
            ? (hints.GetValueOrDefault("latitude"), hints.GetValueOrDefault("longitude"), null, false)
            : FindPoint(columns, items.Count);
        var id = hints.GetValueOrDefault("id") ?? FindId(columns, items.Count);
        var address = hints.GetValueOrDefault("address") ?? FindAddress(columns);
        var postcode = hints.GetValueOrDefault("postcode") ?? FindByValues(columns, items.Count, c => IsPostcode(Text(c)), ["postal", "zip", "postcode", "codepostal", "cp"]);
        var postcodeInAddress = postcode is null && address is not null
            && items.Count(i => PostcodeIn(Text(address.First(i))) is not null) * 2 >= items.Count;
        var province = hints.GetValueOrDefault("province")
            ?? FindByValues(columns, items.Count, c => ProvinceCode(Text(c)) is not null, ["state", "province", "prov", "region"], minShare: 0.3);
        var country = hints.GetValueOrDefault("country")
            ?? FindByValues(columns, items.Count, c => CountryCode(Text(c)) is not null, ["country", "pays"], minShare: 0.3, nameRequired: true);
        var brand = hints.GetValueOrDefault("brand") ?? FindBrand(columns);
        var link = hints.GetValueOrDefault("link") ?? FindLink(columns);

        var fuel = hints.TryGetValue("fuel", out var fuelHint)
            ? new FuelMapping(fuelHint, MapPart(Columns(items.SelectMany(i => fuelHint.Select(i).OfType<JsonObject>()).ToList()), hints: null, fuel: true))
            : hints.ContainsKey("hours") ? null : FindFuel(columns, items);

        var site = MapPart(columns, hints) with { Brand = null };
        var tags = hints.GetValueOrDefault("allDayTags") ?? FindAllDayTags(columns);

        // Every other field named for the country and holding ones (Ultramar's country and siteCountry): all must say Canada.
        var otherCountries = columns
            .Where(c => c.Path.Text != country?.Text && c.Present > 0 && (c.Name.Contains("country", StringComparison.Ordinal) || c.Name.Contains("pays", StringComparison.Ordinal))
                && c.Values.Count(v => CountryCode(Text(v)) is not null) >= 0.8 * c.Present)
            .Select(c => c.Path)
            .ToList();
        return new PluginMapping(
            recordsPath, id, latitude, longitude, point, swapped, address, postcode, postcodeInAddress, province, country, brand, link, site, tags, fuel)
        {
            OtherCountries = otherCountries,
        };
    }

    /// <summary>
    /// A part's hours (fuel-named first), 24-hour flag, status and brand. A field of another department (a car wash's, a
    /// store's: <see cref="GenericHoursReader.IsDepartment"/>), or inside one, is never taken for them.
    /// </summary>
    private static PartMapping MapPart(IReadOnlyList<Column> columns, IReadOnlyDictionary<string, NodePath>? hints, bool fuel = false)
    {
        // The station's own fields only: each inside fields named for the place ("location", "site"), never another
        // department's (a car wash's, a Tim Hortons'): a field inside one of those is that one's.
        columns = [.. columns.Where(c => !c.Keys.Any(GenericHoursReader.IsDepartment) && c.Keys[..^1].All(IsPlaceName))];
        var (hours, fuelHours) = hints?.GetValueOrDefault("hours") is { } hoursHint ? ([hoursHint], 1) : FindHours(columns);
        var allDay = hints?.GetValueOrDefault("allDay")
            ?? columns.FirstOrDefault(c => c.Keys.Length <= 2 && c.Present > 0 && GenericHoursReader.IsAllDayFlag(c.Keys[^1]) && Flags(c))?.Path;

        // Named for the status, it's the status, whatever share of its words are known: a word it doesn't know leaves its
        // record out (HoursPluginSource), rather than the whole field being dropped and its record taken as open.
        var status = hints?.GetValueOrDefault("status")
            ?? columns.Where(c => c.Keys.Length <= 2 && (c.Name is "status" or "statut" || c.Name.EndsWith("status", StringComparison.Ordinal) || c.Name.EndsWith("statut", StringComparison.Ordinal))
                    && c.Present > 0 && c.Values.All(v => v is null || v.GetValueKind() is JsonValueKind.String or JsonValueKind.Null)
                    && c.Values.Any(v => PluginStatusWords.Read(Text(v)) is not PluginStatus.Unknown))
                .Select(c => c.Path).FirstOrDefault();
        var closed = columns.FirstOrDefault(c => c.Keys.Length <= 2 && c.Present > 0
            && c.Name is "temporarilyclosed" or "istemporarilyclosed" or "closedtemporarily" or "temporaryclosure" or "fermetemporairement"
            && Flags(c))?.Path;
        var brand = FindBrand(columns);

        // The forecourt's own hours are all the fuel's.
        return new PartMapping(hours, allDay, status, closed, brand, fuel ? hours.Count : fuelHours) { Exceptions = FindExceptions(columns) };

        static bool Flags(Column column) => column.Values.All(v => v is null || v.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);
    }

    /// <summary>
    /// Words a field of the station's own hours is made of, and nothing else: <c>hours</c>, <c>openingHours</c>,
    /// <c>siteRegularHours</c>, <c>gasStationHours</c>, <c>openingHoursFuel</c>, <c>heuresDOuverture</c>. A field named for
    /// anything else (<c>timHortonsHours</c>, <c>epicerieHours</c>) may be another department's: it needs a hint.
    /// </summary>
    private static readonly string[] HoursWords =
    [
        "openinghoursspecification", "openinghours", "hoursofoperation", "operatinghours", "businesshours", "hours", "hour", "heures",
        "heure", "horaires", "horaire", "schedules", "schedule", "periods", "weekdaytext", "opening", "ouverture",
    ];

    /// <summary>The words of a field named for the place, the forecourt or its regular week, around the hours' words.</summary>
    private static readonly string[] PlaceWords =
    [
        "site", "station", "location", "place", "regular", "normal", "standard", "usual", "default", "main", "general", "weekly", "week",
        "semaine", "service", "services", "fuel", "gas", "gasoline", "forecourt", "pumps", "pump", "pompes", "pompe", "carburant", "essence",
        "petrol", "info", "details", "detail", "data", "list", "text", "spec", "specification", "of", "de", "du", "des", "d",
    ];

    /// <summary>Special hours: dated exceptions to the week, which an undated one would contradict.</summary>
    private static readonly string[] ExceptionWords = ["special", "exceptions", "exception", "temporary", "upcoming", "seasonal"];

    /// <summary>A field's name made only of the place's words ("location", "siteDetails"), or none (the record itself).</summary>
    private static bool IsPlaceName(string key) => Words(GenericHoursReader.Normalize(key), [.. PlaceWords], out _);

    /// <summary>A field's name made only of hours' and place's words, one hours' word at least.</summary>
    private static bool IsHoursName(string name, IReadOnlyCollection<string>? also = null) =>
        Words(name, [.. HoursWords, .. PlaceWords, .. also ?? []], out var used) && used.Any(HoursWords.Contains);

    /// <summary>The name is these words one after the other, and nothing else: the words used, when it is.</summary>
    private static bool Words(string name, IReadOnlyList<string> words, out IReadOnlyList<string> used)
    {
        // reach[i]: the words that make name[..i], or null when none do.
        var reach = new List<string>?[name.Length + 1];
        reach[0] = [];
        for (var i = 0; i < name.Length; i++)
        {
            if (reach[i] is not { } before)
            {
                continue;
            }

            foreach (var word in words.Where(w => string.CompareOrdinal(name, i, w, 0, w.Length) == 0))
            {
                reach[i + word.Length] ??= [.. before, word];
            }
        }

        used = reach[name.Length] ?? [];
        return reach[name.Length] is not null;
    }

    /// <summary>The fields of special hours (schema.org's <c>specialOpeningHoursSpecification</c>), read for their dates.</summary>
    private static IReadOnlyList<NodePath> FindExceptions(IReadOnlyList<Column> columns) =>
        [.. columns.Where(c => c.Keys.Length <= 2 && c.Present > 0 && IsHoursName(c.Name, ExceptionWords) && ExceptionWords.Any(w => c.Name.Contains(w, StringComparison.Ordinal)))
            .Select(c => c.Path)];

    /// <summary>The fields named for hours, those named for the fuel first, and how many those are.</summary>
    private static (IReadOnlyList<NodePath> Hours, int Fuel) FindHours(IReadOnlyList<Column> columns)
    {
        var named = columns
            .Where(c => c.Keys.Length <= 2 && c.Present > 0
                && IsHoursName(c.Name)
                && !GenericHoursReader.IsAllDayFlag(c.Keys[^1])
                && c.Values.All(v => v is null or JsonArray or JsonObject || v.GetValueKind() is JsonValueKind.String or JsonValueKind.Null))
            .ToList();

        // A field inside another one found (openingHours.monday) is that one's.
        named = named.Where(c => !named.Any(o => o != c && c.Path.Text.StartsWith(o.Path.Text + ".", StringComparison.Ordinal))).ToList();
        var ordered = named
            .Select(c => (Column: c, Fuel: FuelNameParts.Any(p => c.Name.Contains(p, StringComparison.Ordinal))))
            .OrderByDescending(c => c.Fuel)
            .ThenByDescending(c => c.Column.Present)
            .ToList();
        return ([.. ordered.Select(c => c.Column.Path)], ordered.Count(c => c.Fuel));
    }

    private static (NodePath? Latitude, NodePath? Longitude, NodePath? Point, bool Swapped) FindPoint(IReadOnlyList<Column> columns, int count)
    {
        var latitudes = columns.Where(c => IsLatitudeName(c.Name) && Numeric(c, -90, 90) && c.Present * 2 >= count).ToList();
        var longitudes = columns.Where(c => IsLongitudeName(c.Name) && Numeric(c, -180, 180) && c.Present * 2 >= count).ToList();
        var pairs = latitudes
            .SelectMany(lat => longitudes.Select(lon => (Lat: lat, Lon: lon)))
            .OrderByDescending(p => p.Lat.Parent == p.Lon.Parent)
            .ThenByDescending(p => Math.Min(p.Lat.Present, p.Lon.Present))
            .ToList();
        if (pairs.Count > 0)
        {
            var (lat, lon) = pairs[0];

            // Québec's latitudes are positive and its longitudes negative: fields holding each other's are swapped.
            var swapped = Share(lat, v => v < -50) > 0.8 && Share(lon, v => v is > 35 and < 70) > 0.8;
            return swapped ? (lon.Path, lat.Path, null, true) : (lat.Path, lon.Path, null, false);
        }

        var geo = columns.FirstOrDefault(c => c.Name == "coordinates" && c.Present * 2 >= count
            && c.Values.All(v => v is JsonArray { Count: 2 } a && a.All(n => Number(n) is not null)));
        return (null, null, geo?.Path, false);
    }

    private static NodePath? FindId(IReadOnlyList<Column> columns, int count)
    {
        return columns
            .Where(c => c.Keys.Length == 1 && c.Present == count && Priority(c.Name) is not null
                && c.Values.All(v => Scalar(v) is { Length: > 0 }) && c.Values.Select(Scalar).Distinct(StringComparer.Ordinal).Count() == count)
            .OrderBy(c => Priority(c.Name))
            .Select(c => c.Path)
            .FirstOrDefault();

        static int? Priority(string name) =>
            name is "id" ? 0
            : name is "@id" || name.EndsWith("id", StringComparison.Ordinal) ? 1
            : name is "key" or "ref" or "identifier" or "uuid" or "guid" ? 2
            : (name.EndsWith("number", StringComparison.Ordinal) || name.EndsWith("num", StringComparison.Ordinal) || name.EndsWith("code", StringComparison.Ordinal))
                && !name.Contains("postal", StringComparison.Ordinal) && !name.Contains("zip", StringComparison.Ordinal) && !name.Contains("phone", StringComparison.Ordinal) ? 3
            : null;
    }

    private static NodePath? FindAddress(IReadOnlyList<Column> columns) =>
        columns
            .Where(c => (c.Name.Contains("address", StringComparison.Ordinal) || c.Name.Contains("adresse", StringComparison.Ordinal)
                    || c.Name is "street" or "rue" or "line1" or "streetaddress")
                && !c.Name.EndsWith('2') && !c.Name.EndsWith('3')
                && !new[] { "email", "mail", "url", "web", "ip", "postal", "zip", "city", "ville", "region", "country", "locality" }.Any(p => c.Name.Contains(p, StringComparison.Ordinal))
                && c.Present > 0 && c.Values.All(v => v is null || v is JsonValue s && s.GetValueKind() is JsonValueKind.String or JsonValueKind.Null))
            .Select(c => (Column: c, Score: (double)c.Values.Count(v => HoursMatcher.HouseNumber(Text(v)) is not null) / c.Present))
            .Where(c => c.Score >= 0.5)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Column.Present)
            .Select(c => c.Column.Path)
            .FirstOrDefault();

    /// <summary>A text field most of whose values pass <paramref name="accept"/>, named so first.</summary>
    private static NodePath? FindByValues(
        IReadOnlyList<Column> columns, int count, Func<JsonNode?, bool> accept, string[] names, double minShare = 0.3, bool nameRequired = false) =>
        columns
            .Where(c => c.Present >= minShare * count && c.Values.Count(accept) >= 0.8 * c.Present
                && (!nameRequired || names.Any(n => c.Name.Contains(n, StringComparison.Ordinal))))
            .OrderByDescending(c => names.Any(n => c.Name.Contains(n, StringComparison.Ordinal)))
            .ThenByDescending(c => c.Present)
            .ThenBy(c => c.Keys.Length)
            .Select(c => c.Path)
            .FirstOrDefault();

    private static NodePath? FindBrand(IReadOnlyList<Column> columns)
    {
        return columns
            .Where(c => c.Keys.Length <= 2 && Priority(c.Name) is not null && c.Present > 0
                && c.Values.All(v => v is null || v is JsonValue s && s.GetValueKind() is JsonValueKind.String or JsonValueKind.Null))
            .OrderBy(c => Priority(c.Name))
            .ThenBy(c => c.Keys.Length)
            .ThenBy(c => c.Name.Length)
            .Select(c => c.Path)
            .FirstOrDefault();

        // A loyalty program's, a card's or a partner's brand isn't the station's.
        static int? Priority(string name) =>
            new[] { "loyalty", "reward", "card", "payment", "partner", "program", "store", "cstore", "restaurant" }.Any(p => name.Contains(p, StringComparison.Ordinal)) ? null
            : name is "brand" or "brandname" or "banner" or "banniere" or "marque" or "enseigne" ? 0
            : name.EndsWith("brand", StringComparison.Ordinal) ? 1
            : name is "brandkey" or "brandslug" or "brandcode" ? 2
            : null;
    }

    private static NodePath? FindLink(IReadOnlyList<Column> columns)
    {
        string[] excluded = ["image", "img", "logo", "icon", "photo", "thumb", "picture", "map", "direction", "facebook", "twitter", "instagram", "menu", "pdf", "career", "apply", "avatar", "banner"];
        return columns
            .Where(c => c.Keys.Length <= 2 && c.Present > 0
                && new[] { "url", "link", "website", "web", "page", "href", "permalink" }.Any(p => c.Name.Contains(p, StringComparison.Ordinal))
                && !excluded.Any(p => c.Name.Contains(p, StringComparison.Ordinal))
                && c.Values.Count(v => Text(v) is { } t && (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) >= 0.8 * c.Present)
            .OrderByDescending(c => c.Name is "url" or "link" or "website" or "permalink" or "landingpageurl" or "pageurl" or "storeurl" or "detailurl" or "detailsurl")
            .ThenByDescending(c => c.Present)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    /// <summary>The names of a list of the station's services or tags: <c>services</c>, <c>amenities</c>, <c>features</c>…</summary>
    private static readonly string[] TagListWords =
    [
        "services", "service", "tags", "tag", "amenities", "amenity", "features", "feature", "attributes", "attribute", "options", "option",
        "facilities", "facility", "caracteristiques", "commodites", "info", "details", "station", "site", "list",
    ];

    /// <summary>A list of services or tags, of which some record's says it's open 24 hours: Harnois' <c>services</c>, never a car wash's.</summary>
    private static NodePath? FindAllDayTags(IReadOnlyList<Column> columns) =>
        columns
            .Where(c => c.Keys.Length <= 2 && c.Keys[..^1].All(IsPlaceName) && Words(c.Name, TagListWords, out _) && c.Values.Any(v => Tags(v).Any(IsAllDayTag)))
            .Select(c => c.Path)
            .FirstOrDefault();

    /// <summary>
    /// The texts of a list of the station's tags: an array of texts; an object of texts by keys that say nothing of whose
    /// they are ("0", "service1", "tag"); an array of objects with a name, and a type, if any, that's the station's or its
    /// fuel's. A tag of anything else (<c>{"carWash": "24h"}</c>, <c>{"timHortons": "24h"}</c>, <c>{"type": "Épicerie",
    /// "name": "24h"}</c>) may be another department's: it isn't the station's.
    /// </summary>
    public static IEnumerable<string> Tags(JsonNode? node) => node switch
    {
        JsonArray array => array.SelectMany(i => i is JsonObject o
            ? !StationsItem(o)
                ? []
                : new[] { "name", "label", "title", "value", "text" }.Select(k => o.FirstOrDefault(m => GenericHoursReader.Normalize(m.Key) == k).Value).Select(Text).OfType<string>().Take(1)
            : Text(i) is { } t && !GenericHoursReader.IsDepartment(t) ? [t] : []),
        JsonObject obj when obj.All(m => m.Value is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False) =>
            obj.Where(m => OpaqueTagKeyPattern().IsMatch(GenericHoursReader.Normalize(m.Key))).Select(m => Text(m.Value)).OfType<string>(),
        _ => [],
    };

    /// <summary>An item of a list of tags with no type, or one that's the station's or its fuel's, and nothing of another department.</summary>
    private static bool StationsItem(JsonObject item) =>
        !item.Any(m => GenericHoursReader.IsDepartment(m.Key) || Text(m.Value) is { } v && GenericHoursReader.IsDepartment(v))
        && item.Where(m => TypeKeys.Contains(GenericHoursReader.Normalize(m.Key)) && GenericHoursReader.Normalize(m.Key) is not ("name" or "label" or "service"))
            .All(m => Text(m.Value) is { } type && GenericHoursReader.Normalize(type) is var t && (FuelWords.Contains(t) || t is "station" or "site" or "general" or "hours" or "horaire"));

    /// <summary>The fuel's part of each record: the items of a list whose type is the forecourt's, the fuel's or the gas bar's.</summary>
    private static FuelMapping? FindFuel(IReadOnlyList<Column> columns, IReadOnlyList<JsonObject> records)
    {
        foreach (var column in columns.Where(c => c.Keys.Length == 1 && c.Values.Any(v => v is JsonArray a && a.Any(i => i is JsonObject))))
        {
            var found = new List<(string Key, string Value, JsonObject Item)>();
            var withFuel = 0;
            foreach (var value in column.Values)
            {
                var fuelItems = (value as JsonArray ?? []).OfType<JsonObject>()
                    .SelectMany(item => item.Where(m => TypeKeys.Contains(GenericHoursReader.Normalize(m.Key)) && IsFuel(Text(m.Value)))
                        .Select(m => (m.Key, Text(m.Value)!, item)).Take(1))
                    .ToList();
                withFuel += fuelItems.Count > 0 ? 1 : 0;
                found.AddRange(fuelItems);
            }

            if (withFuel == 0 || withFuel < 0.3 * records.Count)
            {
                continue;
            }

            var (key, type) = found.GroupBy(f => (f.Key, f.Value)).OrderByDescending(g => g.Count()).First().Key;
            var path = NodePath.Parse($"{column.Path.Text}[{key}={type}]");
            var part = MapPart(Columns([.. found.Where(f => f.Key == key && f.Value == type).Select(f => f.Item)]), hints: null, fuel: true);
            if (!part.IsEmpty)
            {
                return new FuelMapping(path, part);
            }
        }

        return null;

        static bool IsFuel(string? text) => text is not null && FuelWords.Contains(GenericHoursReader.Normalize(text));
    }

    private static bool IsLatitudeName(string name) =>
        name is "lat" or "latitude" || name.EndsWith("latitude", StringComparison.Ordinal) || name.Length <= 10 && name.EndsWith("lat", StringComparison.Ordinal);

    private static bool IsLongitudeName(string name) =>
        name is "lng" or "lon" or "long" or "longitude" || name.EndsWith("longitude", StringComparison.Ordinal)
        || name.Length <= 10 && (name.EndsWith("lng", StringComparison.Ordinal) || name.EndsWith("lon", StringComparison.Ordinal));

    /// <summary>A record with a point: a latitude and a longitude by name within three levels, or a GeoJSON point.</summary>
    private static bool HasPoint(JsonObject item)
    {
        var columns = Columns([item]);
        return columns.Any(c => IsLatitudeName(c.Name) && Numeric(c, -90, 90)) && columns.Any(c => IsLongitudeName(c.Name) && Numeric(c, -180, 180))
            || columns.Any(c => c.Name == "coordinates" && c.Values.All(v => v is JsonArray { Count: 2 } a && a.All(n => Number(n) is not null)));
    }

    private static bool IsKeyed(JsonObject obj) =>
        obj.Count >= 2 && obj.Count(m => m.Value is JsonObject) >= 0.8 * obj.Count;

    private static bool IsPathable(string key) => key.Length > 0 && key.IndexOfAny(['.', '[', ']', '|', '=']) < 0 && key.Trim() == key;

    private static bool IsPostcode(string? text) => text is not null && PostcodePattern().IsMatch(text.Trim());

    private static bool Numeric(Column column, double min, double max) =>
        column.Present > 0 && column.Values.Count(v => Number(v) is { } n && n >= min && n <= max && n != 0) >= 0.9 * column.Present;

    private static double Share(Column column, Func<double, bool> test) =>
        column.Present == 0 ? 0 : (double)column.Values.Count(v => Number(v) is { } n && test(n)) / column.Present;

    public static double? Number(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.GetValue<double>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.String
            && double.TryParse(value.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A string or a number as text: an id.</summary>
    public static string? Scalar(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Trim(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        }
        : null;

    private static string Squeeze(string text)
    {
        var folded = StationKey.Fold(text);
        return string.Concat(folded.Where(char.IsAsciiLetter));
    }

    /// <summary>Every field of the records within three levels of objects, each with the values the records have there.</summary>
    private static List<Column> Columns(IReadOnlyList<JsonObject> items)
    {
        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            Walk(item, []);
        }

        return [.. columns.Values];

        void Walk(JsonObject obj, string[] prefix)
        {
            foreach (var (key, value) in obj)
            {
                if (!IsPathable(key))
                {
                    continue;
                }

                string[] keys = [.. prefix, key];
                var text = string.Join('.', keys);
                if (!columns.TryGetValue(text, out var column))
                {
                    columns[text] = column = new Column(keys);
                }

                column.Add(value);
                if (value is JsonObject child && keys.Length < MaxDepth)
                {
                    Walk(child, keys);
                }
            }
        }
    }

    private sealed class Column(string[] keys)
    {
        public string[] Keys { get; } = keys;
        public NodePath Path { get; } = NodePath.Of(keys);
        public string Name { get; } = GenericHoursReader.Normalize(keys[^1]);
        public string Parent { get; } = string.Join('.', keys[..^1]);
        public List<JsonNode?> Values { get; } = [];
        public int Present => Values.Count(v => v is not null && !(v is JsonValue n && n.GetValueKind() == JsonValueKind.Null));

        public void Add(JsonNode? value) => Values.Add(value);
    }

    [GeneratedRegex(@"^[A-Za-z]\d[A-Za-z][ -]?\d[A-Za-z]\d$")]
    private static partial Regex PostcodePattern();

    [GeneratedRegex(@"\b[A-Za-z]\d[A-Za-z][ -]?\d[A-Za-z]\d\b")]
    private static partial Regex PostcodeSearch();

    [GeneratedRegex(@"^(?:ouvert\s*|open\s*)?(?:24\s*(?:h|hr|hrs|heures?|hours?)(?:\s*(?:/|sur)\s*24)?|24\s*/\s*7)$")]
    private static partial Regex AllDayTagPattern();

    /// <summary>A tag's key that says nothing of whose the tag is: "0", "12", "service1", "tag", "item3".</summary>
    [GeneratedRegex(@"^(?:\d+|(?:service|tag|item|feature|amenity|attribute|option|value|text|code|id|key)s?\d*)$")]
    private static partial Regex OpaqueTagKeyPattern();
}

/// <summary>What a status says: open, closed for now, closed for good, or a word it doesn't know.</summary>
public enum PluginStatus
{
    Unknown,
    Open,
    TemporarilyClosed,
    PermanentlyClosed,
}

public static class PluginStatusWords
{
    private static readonly string[] Open = ["active", "open", "opened", "operational", "ouvert", "ouverte", "enoperation", "inoperation", "actif", "active"];
    private static readonly string[] Temporary = ["temporaryclosure", "temporarilyclosed", "closedtemporarily", "fermetemporairement", "fermeetemporairement", "fermeturetemporaire", "temporaryclosed"];
    private static readonly string[] Permanent = ["permanentlyclosed", "closedpermanently", "closed", "fermedefinitivement", "fermeedefinitivement", "fermeturedefinitive", "ferme", "fermee", "inactive", "inactif"];

    public static PluginStatus Read(string? text)
    {
        var word = text is null ? "" : GenericHoursReader.Normalize(text);
        return Open.Contains(word) ? PluginStatus.Open
            : Temporary.Contains(word) ? PluginStatus.TemporarilyClosed
            : Permanent.Contains(word) ? PluginStatus.PermanentlyClosed
            : PluginStatus.Unknown;
    }
}
