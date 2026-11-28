using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Essence.Data;
using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>
/// An hours source written as a spec (<see cref="HoursPluginSpec"/>), run like the others: on the weekly schedule, its
/// last download kept and matched again when the stations change, its runs gated and alerted on. A run asks the spec's
/// URL (once per <see cref="HoursPluginSpec.Params"/>), reads the answer whatever its format (<see cref="PluginDocument"/>),
/// finds its records and their fields (<see cref="PluginMapper"/>), and keeps on disk only what it read of each record,
/// in Québec: never its phone or its people. Reading the hours (<see cref="GenericHoursReader"/>) is apart, so a better
/// reader applies to the download kept at the next re-match. A record without a point is left out: without one, it
/// would be its brand's rule (<see cref="HoursMatcher"/>).
/// </summary>
public sealed partial class HoursPluginSource(HoursPluginSpec spec, HoursClient client, HoursPacer pacer, IOptions<HoursOptions> options) : IHoursSource
{
    /// <summary>What the download kept on disk is: a plugin's records as read, not a source's answer.</summary>
    public const string Format = "essence-hours-plugin/1";

    /// <summary>
    /// The version of what a download keeps (<see cref="Extract"/> and <see cref="PluginMapper"/>): raised when a change to
    /// them would keep other records or fields from the same answer, so every plugin's download is made again at the next
    /// start (<see cref="Kept"/>). That run is still gated against the last good one: the spec, the owner's, didn't change.
    /// </summary>
    public const int Engine = 1;

    public HoursPluginSpec Spec => spec;

    public string Name => spec.Name;
    public bool Enabled => spec.Enabled && options.Value.Plugins.GetValueOrDefault(spec.Name)?.Enabled != false;
    public Essence.Ranking.HoursSourceProfile Profile => new(spec.Label, spec.Kind, spec.Rank);
    public bool ShippedInImage => false;
    public double MatchRadiusM => spec.MatchRadiusM;
    public double? OtherBrandReachM => spec.OtherBrandReachM;

    /// <summary>The URLs in turn, a pause between them; the records put together, each id once. Any request that fails fails the run.</summary>
    public async Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var (raw, _) = await DownloadAsync(cancellationToken);
        var version = HoursVersion.Of(raw);
        return version == cachedVersion ? new HoursFetch.NotModified() : new HoursFetch.Fetched(raw, version);
    }

    /// <summary>The download as kept, and its mappings (one per URL), for <see cref="FetchAsync"/> and the try command.</summary>
    public async Task<(byte[] Raw, IReadOnlyList<PluginMapping> Mappings)> DownloadAsync(CancellationToken cancellationToken)
    {
        var records = new JsonArray();
        var described = new JsonArray();
        var mappings = new List<PluginMapping>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var urls = spec.Urls();
        for (var i = 0; i < urls.Count; i++)
        {
            if (i > 0)
            {
                await pacer.PauseAsync(cancellationToken);
            }

            var (mapping, read) = Extract(await client.GetAsync(urls[i], cancellationToken), spec);
            mappings.Add(mapping);
            described.Add(mapping.Describe());
            foreach (var record in read.Where(r => seen.Add((string)r["id"]!)))
            {
                records.Add(record);
            }
        }

        return (
            HoursJson.Bytes(new JsonObject { ["format"] = Format, ["engine"] = Engine, ["spec"] = Fingerprint(spec), ["mappings"] = described, ["records"] = records }),
            mappings);
    }

    /// <summary>
    /// What of the spec made the download kept: its URLs, format, province, filters and hints. Its label, brand, link and
    /// unknown ranges are read again from the spec at each read, so they're not in it. Kept with the last good run too
    /// (<c>hours_sources.good_spec</c>), so the gate knows a run made under another spec (<see cref="HoursGate.Baseline"/>).
    /// </summary>
    public static string Fingerprint(HoursPluginSpec spec)
    {
        var text = new StringBuilder(Format).Append('\n');
        foreach (var url in spec.Urls())
        {
            text.Append(url.AbsoluteUri).Append('\n');
        }

        text.Append(spec.Format).Append('\n').Append(spec.Province ?? "*").Append('\n');
        foreach (var filter in spec.Where)
        {
            text.Append(filter.Path.Text).Append('=').AppendJoin(',', filter.Values.Select(v => v?.ToJsonString() ?? "null")).Append('\n');
        }

        foreach (var (field, path) in spec.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            text.Append(field).Append(':').Append(path.Text).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    public string SpecFingerprint => Fingerprint(spec);

    public DownloadKept Kept(byte[] raw)
    {
        try
        {
            return HoursJson.Parse(raw, "") is not JsonObject root || PluginMapper.Text(root["format"]) != Format ? DownloadKept.OtherFormat
                : PluginMapper.Text(root["spec"]) != Fingerprint(spec) ? DownloadKept.OtherSpec
                : PluginMapper.Number(root["engine"]) != Engine ? DownloadKept.OtherEngine
                : DownloadKept.Current;
        }
        catch (HoursFormatException)
        {
            return DownloadKept.OtherFormat;
        }
    }

    /// <summary>One answer's records, in the spec's province, as kept: their fields where the mapping found them.</summary>
    /// <exception cref="HoursFormatException">The answer can't be read, says more pages follow, or has no records with a point.</exception>
    public static (PluginMapping Mapping, IReadOnlyList<JsonObject> Records) Extract(byte[] body, HoursPluginSpec spec)
    {
        var what = $"The answer of {spec.Label}";
        var document = PluginDocument.Read(body, spec.Format, what);
        if (PluginMapper.SaysFailed(document))
        {
            throw new HoursFormatException($"{what} says it failed (\"success\": false): what it holds may be partial.");
        }

        if (PluginMapper.HasMorePages(document))
        {
            throw new HoursFormatException($"{what} says more pages follow: ask for more at once in the URL, or for each page in \"params\".");
        }

        var (path, found) = PluginMapper.Records(document, spec.Fields.GetValueOrDefault("records"), what);
        var items = found.Where(i => spec.Where.All(f => f.Matches(i.Item))).ToList();
        if (items.Count == 0)
        {
            return (PluginMapping.Empty(path), []);
        }

        var mapping = PluginMapper.Map(path, items, spec.Fields);
        if (mapping.Latitude is null && mapping.Point is null)
        {
            throw new HoursFormatException($"{what}: no latitude and longitude found among its records' fields; give them in the spec's \"fields\".");
        }

        var records = new List<JsonObject>();
        foreach (var (key, item) in items)
        {
            var (latitude, longitude) = Point(item, mapping);
            var address = PluginMapper.Text(mapping.Address?.First(item));
            var postcode = mapping.PostcodeInAddress ? PluginMapper.PostcodeIn(address) : PluginMapper.Text(mapping.Postcode?.First(item));
            var province = PluginMapper.ProvinceCode(PluginMapper.Text(mapping.Province?.First(item))) ?? PluginMapper.ProvinceOfPostcode(postcode);
            // Every field of the country must say Canada: "US", "FR" or a word it doesn't know leave the record out.
            if (spec.Province is { } wanted && province is not null && province != wanted
                || new[] { mapping.Country }.Concat(mapping.OtherCountries).OfType<NodePath>()
                    .Any(c => PluginMapper.Text(c.First(item)) is { } said && PluginMapper.CountryCode(said) != "CA"))
            {
                continue;
            }

            var id = PluginMapper.Scalar(mapping.Id?.First(item)) is { Length: > 0 } given ? given
                : key ?? (latitude is { } lat && longitude is { } lon ? string.Create(CultureInfo.InvariantCulture, $"{lat:0.00000},{lon:0.00000}") : null);
            if (id is null)
            {
                continue;
            }

            var tags = mapping.AllDayTags is { } tagPath && PluginMapper.Tags(tagPath.First(item)).Any(PluginMapper.IsAllDayTag);
            var record = new JsonObject
            {
                ["id"] = id,
                ["lat"] = latitude,
                ["lon"] = longitude,
                ["address"] = address,
                ["postcode"] = postcode,
                ["province"] = province,
                ["brand"] = PluginMapper.Text(mapping.Brand?.First(item)),
                ["link"] = PluginMapper.Text(mapping.Link?.First(item)) is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? link : null,
                ["site"] = Part(item, mapping.Site, tags),
            };
            // Neither a place nor hours: a page's other data (schema.org's WebPage), not a record worth a warning.
            if (latitude is null && longitude is null && record["site"]!["hours"] is JsonArray { Count: 0 } && mapping.Fuel is null)
            {
                continue;
            }

            if (mapping.Fuel is { } fuel && fuel.Items.First(item) is JsonObject fuelItem)
            {
                record["fuel"] = Part(fuelItem, fuel.Part, tags: false);
            }

            records.Add(record);
        }

        return (mapping, records);
    }

    /// <summary>
    /// The records of a download kept, each with its hours read. A record is left out, with a warning, when it has no
    /// point, the same id as another, a status that isn't understood or that says it's closed for good, a forecourt open
    /// while it says it's closed for now (which is right can't be told), or hours that can't be read for sure; those
    /// without hours are counted in one warning. The fuel's part comes first when it gives hours, and its status too.
    /// </summary>
    public HoursParse Parse(byte[] raw)
    {
        if (HoursJson.Parse(raw, $"The download of {spec.Label} kept on disk") is not JsonObject root
            || PluginMapper.Text(root["format"]) != Format || root["records"] is not JsonArray all)
        {
            throw new HoursFormatException($"The download of {spec.Label} kept on disk isn't a plugin's records: it's matched again once a run keeps one.");
        }

        if (PluginMapper.Text(root["spec"]) != Fingerprint(spec) || PluginMapper.Number(root["engine"]) != Engine)
        {
            throw new HoursFormatException(
                $"The download of {spec.Label} kept on disk was made by another version of its spec (its URL, filters or hints changed) or of the engine: it's matched again once a run keeps one.");
        }

        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var withoutHours = 0;
        foreach (var record in all.OfType<JsonObject>())
        {
            var id = PluginMapper.Text(record["id"]);
            var address = PluginMapper.Text(record["address"]);
            var what = $"{spec.Label} {id ?? "?"}{(address is null ? "" : $" ({address})")}";
            if (id is null || !seen.Add(id))
            {
                warnings.Add($"{what} has no id, or the same as another; left out.");
                continue;
            }

            var (latitude, longitude) = (PluginMapper.Number(record["lat"]), PluginMapper.Number(record["lon"]));
            if (!HoursJson.IsPoint(latitude, longitude))
            {
                warnings.Add($"{what} has no coordinates; left out.");
                continue;
            }

            var site = record["site"] as JsonObject ?? [];
            var fuel = record["fuel"] as JsonObject;
            var statusError = Status(site, out var siteStatus);
            PluginStatus? fuelStatus = null;
            statusError ??= fuel is null ? null : Status(fuel, out fuelStatus);
            if (statusError is not null)
            {
                warnings.Add($"{what}: {statusError}; left out.");
                continue;
            }

            if (siteStatus == PluginStatus.TemporarilyClosed && fuelStatus == PluginStatus.Open)
            {
                warnings.Add($"{what}: it says it's closed for now, but its forecourt says it's open: which is right can't be told; left out.");
                continue;
            }

            var closed = (fuelStatus ?? siteStatus) == PluginStatus.TemporarilyClosed
                || site["closed"]?.GetValueKind() == JsonValueKind.True || fuel?["closed"]?.GetValueKind() == JsonValueKind.True;

            var fromFuel = fuel is not null && Gives(fuel);
            var (rule, error) = Rule(fromFuel ? fuel! : site);
            if (error is not null)
            {
                warnings.Add($"{what}: {(fromFuel ? "the forecourt's" : "its")} hours can't be read for sure ({error}); left out.");
                continue;
            }

            if (rule is null)
            {
                withoutHours++;
                continue;
            }

            var postcode = StationKey.NormalizePostalCode(PluginMapper.Text(record["postcode"]));
            records.Add(new SourceRecord(
                id,
                BrandFamily.Of(PluginMapper.Text(fuel?["brand"]) ?? PluginMapper.Text(record["brand"]) ?? spec.Brand),
                latitude,
                longitude,
                HoursMatcher.HouseNumber(address),
                postcode is { Length: > 0 } ? postcode : null,
                rule,
                PluginMapper.Text(record["link"]) ?? spec.Link,
                CheckedAt: null,
                TemporarilyClosed: closed));
        }

        if (withoutHours > 0)
        {
            warnings.Add($"{withoutHours} of the {all.Count} places give no hours; left out.");
        }

        return new HoursParse(records, warnings);
    }

    /// <summary>
    /// A part's rule: the first of its fuel's (or given) hours fields that gives hours; else its other fields', which must
    /// agree (schema.org's <c>openingHours</c> and <c>openingHoursSpecification</c> say the same): two that give different
    /// rules, or one that can't be read, refuse it, which is the station's hours being unknown.
    /// </summary>
    private (string? Rule, string? Error) Rule(JsonObject part)
    {
        if (part["undatedExceptions"]?.GetValueKind() == JsonValueKind.True)
        {
            return (null, "its special hours aren't dated, so whether they're the week's can't be told");
        }

        var allDay = part["allDay"]?.GetValueKind() == JsonValueKind.True;
        var fuelHours = PluginMapper.Number(part["fuelHours"]) is { } count ? (int)count : 0;
        var read = HoursRead.None;
        var index = -1;
        foreach (var hours in part["hours"] as JsonArray ?? [])
        {
            index++;
            var next = GenericHoursReader.Read(hours, spec.UnknownRanges);
            if (next.Outcome == HoursReadOutcome.None)
            {
                continue;
            }

            if (index < fuelHours || next.Outcome == HoursReadOutcome.Refused)
            {
                read = next;
                break;
            }

            if (read.Outcome == HoursReadOutcome.Rule && next.Rule != read.Rule)
            {
                return (null, $"two of its fields give different hours, {read.Rule} and {next.Rule}: which are the station's can't be told");
            }

            read = next;
        }

        var always = OpeningHours.AlwaysOpen.ToRule();
        return read.Outcome switch
        {
            HoursReadOutcome.Refused => (null, read.Error),
            HoursReadOutcome.Rule when allDay && read.Rule != always => (null, $"it says it's open 24 hours, but its days say {read.Rule}"),
            HoursReadOutcome.Rule => (read.Rule, null),
            _ => (allDay ? always : null, null),
        };
    }

    /// <summary>The part gives hours: some that aren't empty, or that it's open 24 hours.</summary>
    private bool Gives(JsonObject part) =>
        part["allDay"]?.GetValueKind() == JsonValueKind.True
        || (part["hours"] as JsonArray ?? []).Any(h => GenericHoursReader.Read(h, spec.UnknownRanges).Outcome != HoursReadOutcome.None);

    /// <summary>What a part's status says; null when it has none. An error for a status not understood, or closed for good.</summary>
    private static string? Status(JsonObject part, out PluginStatus? status)
    {
        status = null;
        if (part["statusMissing"]?.GetValueKind() == JsonValueKind.True)
        {
            return "it gives no status, where the others give theirs";
        }

        if (PluginMapper.Text(part["status"]) is not { } text)
        {
            return null;
        }

        status = PluginStatusWords.Read(text);
        return status switch
        {
            PluginStatus.Unknown => $"its status \"{text}\" isn't understood",
            PluginStatus.PermanentlyClosed => $"its status \"{text}\" says it's closed for good",
            _ => null,
        };
    }

    private static (double? Latitude, double? Longitude) Point(JsonObject item, PluginMapping mapping)
    {
        if (mapping.Point?.First(item) is JsonArray { Count: 2 } coordinates)
        {
            return (PluginMapper.Number(coordinates[1]), PluginMapper.Number(coordinates[0]));
        }

        return (PluginMapper.Number(mapping.Latitude?.First(item)), PluginMapper.Number(mapping.Longitude?.First(item)));
    }

    /// <summary>A part as kept: its status, its flags and its hours' nodes, each copied.</summary>
    private static JsonObject Part(JsonObject item, PartMapping part, bool tags)
    {
        var allDay = tags || part.AllDay?.First(item)?.GetValueKind() == JsonValueKind.True;
        var hours = part.Hours.Select((h, i) => (Node: h.First(item)?.DeepClone(), Fuel: i < part.FuelHours)).Where(h => h.Node is not null).ToList();
        var status = PluginMapper.Text(part.Status?.First(item));
        return new JsonObject
        {
            ["status"] = status,

            // The others give theirs: one without can't be told open.
            ["statusMissing"] = part.Status is not null && status is null ? true : null,
            ["closed"] = part.ClosedFlag?.First(item)?.GetValueKind() == JsonValueKind.True ? true : null,
            ["allDay"] = allDay,
            ["brand"] = PluginMapper.Text(part.Brand?.First(item)),
            ["hours"] = new JsonArray([.. hours.Select(h => h.Node)]),
            ["fuelHours"] = hours.Count(h => h.Fuel),

            // Special hours that aren't dated ("Sunday: closed" as an exception) would say other hours than the week's.
            ["undatedExceptions"] = part.Exceptions.Any(e => IsUndated(e.First(item))) ? true : null,
        };
    }

    private static readonly string[] DateKeys = ["validfrom", "validthrough", "validfromdate", "validthroughdate", "date", "dates", "startdate", "enddate", "datefrom", "dateto"];

    /// <summary>
    /// Special hours that aren't all dated: entries without a date (<c>validFrom</c>, <c>date</c>…), or text, which can't be
    /// told a holiday's from the week's. None, or an empty list, are dated enough.
    /// </summary>
    private static bool IsUndated(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value => value.GetValueKind() is not JsonValueKind.Null && !(value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim().Length == 0),
        JsonArray array => array.Any(i => i is not JsonObject o || !Dated(o)),
        JsonObject obj => obj.Count > 0 && !Dated(obj) && !obj.All(m => DatePattern().IsMatch(m.Key)),
        _ => true,
    };

    private static bool Dated(JsonObject entry) => entry.Any(m => DateKeys.Contains(GenericHoursReader.Normalize(m.Key)) && m.Value is not null);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial System.Text.RegularExpressions.Regex DatePattern();
}
