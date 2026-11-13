using System.Globalization;
using System.Text.Json;
using Essence.Data;
using Essence.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours;

/// <summary>
/// Hours written down by hand in <c>curated-hours.json</c>, shipped in the assembly: a brand's rule for all its stations
/// (Costco's gas bars, in case its locator can't be fetched), and rules for single stations, found by their address and
/// postal code, which beat their brand's. It downloads nothing, so it runs on each start, and a new image's rules are in
/// at once: a rule taken out of the file takes its rows with it, the file left empty all of them. Only a rule that can't
/// be read keeps the file out, and the rules before it serving.
/// </summary>
public sealed class CuratedHoursSource(IOptions<HoursOptions> options) : IHoursSource
{
    public const string SourceName = "curated";

    private const string ResourceName = "Essence.Ingest.Hours.curated-hours.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string Name => SourceName;
    public bool Enabled => options.Value.Curated.Enabled;
    public bool ShippedInImage => true;

    /// <summary>Unused: curated rules name their station or their brand, never a point.</summary>
    public double MatchRadiusM => 0;

    /// <summary>The file as shipped.</summary>
    public static byte[] Embedded()
    {
        using var stream = typeof(CuratedHoursSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public Task<HoursFetch> FetchAsync(string? cachedVersion, CancellationToken cancellationToken)
    {
        var raw = Embedded();
        return Task.FromResult<HoursFetch>(new HoursFetch.Fetched(raw, HoursVersion.Of(raw)));
    }

    public HoursParse Parse(byte[] raw)
    {
        CuratedFile file;
        try
        {
            file = JsonSerializer.Deserialize<CuratedFile>(raw, Json) ?? throw new HoursFormatException("The curated hours are empty.");
        }
        catch (JsonException ex)
        {
            throw new HoursFormatException($"The curated hours aren't valid JSON: {ex.Message}", ex);
        }

        var records = new List<SourceRecord>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var brand in file.Brands ?? [])
        {
            var family = BrandFamily.Of(brand.Brand);
            var what = $"The curated rule for the brand \"{brand.Brand}\"";
            if (family is null)
            {
                warnings.Add($"{what} names no brand; left out.");
            }
            else if (Read(what, brand, warnings) is { } read && Unique($"brand/{family}", what))
            {
                records.Add(new SourceRecord($"brand/{family}", family, null, null, null, null, read.Rule, brand.Link, read.CheckedAt, false));
            }
        }

        foreach (var station in file.Stations ?? [])
        {
            var what = $"The curated rule for {station.Address} {station.PostalCode}";
            if (string.IsNullOrWhiteSpace(station.Address) || string.IsNullOrWhiteSpace(station.PostalCode))
            {
                warnings.Add($"{what} needs an address and a postal code; left out.");
                continue;
            }

            var key = StationKey.Create(station.Address, station.PostalCode);
            if (Read(what, station, warnings) is { } read && Unique($"station/{key}", what))
            {
                records.Add(new SourceRecord(
                    $"station/{key}", BrandFamily.Of(station.Brand), null, null, null, null, read.Rule, station.Link, read.CheckedAt, false)
                {
                    NaturalKey = key,
                });
            }
        }

        return new HoursParse(records, warnings);

        bool Unique(string reference, string what)
        {
            if (seen.Add(reference))
            {
                return true;
            }

            warnings.Add($"{what} is there twice; the first one counts.");
            return false;
        }
    }

    /// <summary>The rule made canonical and the day it was checked, or null with a warning when either can't be read.</summary>
    private static (string Rule, DateTimeOffset? CheckedAt)? Read(string what, CuratedRule rule, List<string> warnings)
    {
        if (!OpeningHours.TryParse(rule.Rule, lenient: false, out var hours, out var error))
        {
            warnings.Add($"{what} can't be read ({error}); left out.");
            return null;
        }

        DateTimeOffset? checkedAt = null;
        if (rule.Checked is { } day)
        {
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                warnings.Add($"{what} says it was checked on \"{day}\", which isn't a date (yyyy-MM-dd); left out.");
                return null;
            }

            checkedAt = MidnightInQuebec(date);
        }

        return (hours.ToRule(), checkedAt);
    }

    /// <summary>A day in Québec, as when a rule was checked: its midnight there.</summary>
    internal static DateTimeOffset MidnightInQuebec(DateOnly date)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, QuebecTime.Eastern.GetUtcOffset(midnight));
    }

    private class CuratedRule
    {
        public string? Rule { get; init; }

        /// <summary>The day the rule was checked against <see cref="Link"/>, yyyy-MM-dd.</summary>
        public string? Checked { get; init; }

        public string? Link { get; init; }

        /// <summary>For people: where the rule comes from. Not stored.</summary>
        public string? Note { get; init; }
    }

    private sealed class CuratedBrand : CuratedRule
    {
        public string? Brand { get; init; }
    }

    private sealed class CuratedStation : CuratedRule
    {
        public string? Address { get; init; }
        public string? PostalCode { get; init; }

        /// <summary>Optional: the station's brand, for people reading the file.</summary>
        public string? Brand { get; init; }
    }

    private sealed class CuratedFile
    {
        public List<CuratedBrand>? Brands { get; init; }
        public List<CuratedStation>? Stations { get; init; }
    }
}
