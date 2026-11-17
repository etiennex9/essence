using System.Text;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Tests;

public sealed class HoursGateTests
{
    private const double MinShare = 0.8;

    private static readonly IOptions<HoursOptions> Settings = Options.Create(new HoursOptions());
    private static readonly CuratedHoursSource Curated = new(Settings);
    private static readonly CostcoSource Costco = new(new HoursClient(new HttpClient()), Settings);

    [Fact]
    public void A_source_that_gives_no_records_is_rejected_even_the_first_time()
    {
        Assert.Equal("The source gave no records.", HoursGate.Check(0, 0, null, MinShare));
        Assert.NotNull(HoursGate.Check(0, 0, 18, MinShare));
    }

    [Fact]
    public void A_quarter_fewer_stations_than_the_last_good_run_is_rejected()
    {
        Assert.Equal("13 stations matched, fewer than 80 % of the last good run's 18.", HoursGate.Check(18, 13, 18, MinShare));
        Assert.NotNull(HoursGate.Check(400, 300, 400, MinShare));
    }

    [Theory]
    [InlineData(15, 18)]
    [InlineData(8, 10)]
    [InlineData(18, 18)]
    [InlineData(25, 18)]
    public void Down_to_80_percent_of_the_last_good_run_is_accepted(int matched, int lastGood)
    {
        Assert.Null(HoursGate.Check(matched, matched, lastGood, MinShare));
    }

    [Fact]
    public void The_first_run_is_accepted_whatever_it_matches()
    {
        Assert.Null(HoursGate.Check(18, 0, null, MinShare));
        Assert.Null(HoursGate.Check(18, 18, null, MinShare));
    }

    [Fact]
    public void A_curated_file_left_empty_is_accepted_so_its_rows_go()
    {
        var parsed = Curated.Parse(Encoding.UTF8.GetBytes("""{ "brands": [], "stations": [] }"""));

        Assert.Empty(parsed.Records);
        Assert.Null(HoursGate.Check(Curated, parsed, matched: 0, lastGoodMatched: 18, MinShare));
        // The same answer from a source that downloads it is a broken answer.
        Assert.NotNull(HoursGate.Check(Costco, parsed, matched: 0, lastGoodMatched: 18, MinShare));
    }

    [Fact]
    public void A_curated_file_cut_down_to_a_few_station_rules_is_accepted_however_few_it_matches()
    {
        // The brand's rule matched the 18 warehouses; two station rules are left of it.
        var parsed = Curated.Parse(Encoding.UTF8.GetBytes("""
            {
              "stations": [
                { "address": "1 rue Principale", "postalCode": "H1H 1H1", "rule": "Mo-Su 06:00-22:00", "checked": "2026-11-11" },
                { "address": "2 rue Principale", "postalCode": "H1H 1H2", "rule": "24/7", "checked": "2026-11-11" }
              ]
            }
            """));

        Assert.Equal(2, parsed.Records.Count);
        Assert.Null(HoursGate.Check(Curated, parsed, matched: 2, lastGoodMatched: 18, MinShare));
        Assert.NotNull(HoursGate.Check(Costco, parsed, matched: 2, lastGoodMatched: 18, MinShare));
    }

    [Fact]
    public void A_curated_file_with_a_rule_that_can_t_be_read_is_rejected_whole_so_that_rule_s_rows_keep_serving()
    {
        var parsed = Curated.Parse(Encoding.UTF8.GetBytes("""
            {
              "brands": [ { "brand": "Costco", "rule": "Mo-Fr 06:00-21:30; Sa-Su 06:00+" } ],
              "stations": [ { "address": "1 rue Principale", "postalCode": "H1H 1H1", "rule": "24/7" } ]
            }
            """));

        Assert.Single(parsed.Records);
        Assert.Equal(
            "1 of the rules shipped were left out (see the warnings): the file needs fixing.",
            HoursGate.Check(Curated, parsed, matched: 1, lastGoodMatched: null, MinShare));
    }

    [Fact]
    public void The_shipped_curated_file_passes_its_gate()
    {
        var parsed = Curated.Parse(CuratedHoursSource.Embedded());

        Assert.Null(HoursGate.Check(Curated, parsed, matched: 0, lastGoodMatched: 18, MinShare));
    }
}
