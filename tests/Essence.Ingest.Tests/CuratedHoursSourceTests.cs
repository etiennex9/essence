using System.Text;
using Essence.Data;
using Essence.Hours;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Tests;

public sealed class CuratedHoursSourceTests
{
    private readonly CuratedHoursSource _source = new(Options.Create(new HoursOptions()));

    private HoursParse Parse(string json) => _source.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void The_shipped_file_reads_without_a_warning_and_every_rule_in_it_is_valid_and_canonical()
    {
        var parsed = _source.Parse(CuratedHoursSource.Embedded());

        Assert.Empty(parsed.Warnings);
        Assert.NotEmpty(parsed.Records);
        Assert.All(parsed.Records, r =>
        {
            Assert.True(OpeningHours.TryParse(r.Rule, lenient: false, out var hours, out var error), error);
            Assert.Equal(hours.ToRule(), r.Rule);
            Assert.NotNull(r.CheckedAt);
            Assert.StartsWith("https://", r.Link);
        });
    }

    [Fact]
    public void Costco_s_rule_is_its_gas_bars_hours_checked_on_november_11()
    {
        var costco = Assert.Single(_source.Parse(CuratedHoursSource.Embedded()).Records, r => r.BrandFamily == BrandFamily.Costco);

        Assert.Equal("brand/costco", costco.Ref);
        Assert.Equal("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", costco.Rule);
        Assert.Equal("https://www.costco.ca/w/-/locations", costco.Link);
        // Midnight in Québec.
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 5, 0, 0, TimeSpan.Zero), costco.CheckedAt);
        Assert.Null(costco.NaturalKey);
        Assert.Null(costco.Latitude);
    }

    [Fact]
    public async Task Its_version_is_the_SHA_256_of_the_file_and_it_runs_on_each_start()
    {
        var fetched = Assert.IsType<HoursFetch.Fetched>(await _source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(CuratedHoursSource.Embedded(), fetched.Raw);
        Assert.Equal(HoursVersion.Of(CuratedHoursSource.Embedded()), fetched.Version);
        Assert.Matches("^[0-9a-f]{64}$", fetched.Version);
        Assert.True(_source.ShippedInImage);
    }

    [Fact]
    public void A_station_s_rule_is_found_by_its_address_and_postal_code_as_the_feed_writes_them()
    {
        var parsed = Parse("""
            {
              "stations": [
                { "address": "2999 aut. 440,  Laval", "postalCode": "h7p 5p4", "rule": "Mo-Su 06:00-22:00", "checked": "2026-11-11" }
              ]
            }
            """);

        var record = Assert.Single(parsed.Records);
        Assert.Equal(StationKey.Create("2999 aut. 440, Laval", "H7P 5P4"), record.NaturalKey);
        Assert.Equal($"station/{record.NaturalKey}", record.Ref);
        Assert.Equal("Mo-Su 06:00-22:00", record.Rule);
    }

    [Fact]
    public void Rules_are_made_canonical()
    {
        var parsed = Parse("""{ "brands": [ { "brand": "Esso", "rule": "Sa-Su 06:00-20:30; Mo-Fr 06:00-21:30" } ] }""");

        Assert.Equal("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", Assert.Single(parsed.Records).Rule);
    }

    [Fact]
    public void A_rule_that_can_t_be_read_is_left_out_with_a_warning()
    {
        var parsed = Parse("""
            {
              "brands": [
                { "brand": "Shell", "rule": "Mo-Fr 06:00+" },
                { "brand": "Ultramar", "rule": "24/7", "checked": "11/11/2026" },
                { "brand": "", "rule": "24/7" },
                { "brand": "Costco", "rule": "Mo-Su 06:00-21:00" },
                { "brand": "COSTCO", "rule": "24/7" }
              ],
              "stations": [ { "address": "1 rue Principale", "rule": "24/7" } ]
            }
            """);

        Assert.Equal("Mo-Su 06:00-21:00", Assert.Single(parsed.Records).Rule);
        Assert.Equal(5, parsed.Warnings.Count);
        Assert.Contains(parsed.Warnings, w => w.Contains("Shell", StringComparison.Ordinal) && w.Contains("left out", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, w => w.Contains("11/11/2026", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, w => w.Contains("COSTCO", StringComparison.Ordinal) && w.Contains("twice", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, w => w.Contains("postal code", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_isn_t_json_is_refused()
    {
        Assert.Throws<HoursFormatException>(() => Parse("brands: costco"));
    }
}
