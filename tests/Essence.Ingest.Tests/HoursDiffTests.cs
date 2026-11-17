using Essence.Data.Entities;
using Essence.Ingest.Hours;

namespace Essence.Ingest.Tests;

public sealed class HoursDiffTests
{
    private static readonly HoursRow Laval = new(
        "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", "505", CostcoSource.Link, StationHoursEntry.ByDistance, 18, true, false, null);

    [Fact]
    public void A_station_without_a_row_gets_one_a_changed_row_is_updated_and_a_station_no_longer_matched_loses_its_row()
    {
        var existing = new Dictionary<long, HoursRow>
        {
            [1] = Laval,
            [2] = Laval with { SourceRef = "515" },
            [3] = Laval with { SourceRef = "1127" },
        };
        var wanted = new Dictionary<long, HoursRow>
        {
            [1] = Laval,
            [2] = Laval with { SourceRef = "515", OpeningHours = "Mo-Su 06:00-22:00" },
            [4] = Laval with { SourceRef = "1446" },
        };

        var changes = HoursDiff.Compute(existing, wanted);

        Assert.Equal([4L], changes.Added.Keys);
        Assert.Equal("Mo-Su 06:00-22:00", Assert.Single(changes.Updated, u => u.Key == 2).Value.OpeningHours);
        Assert.Equal([3L], changes.Removed);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void The_same_rows_are_no_change_whatever_the_offset_they_were_checked_at_was_written_with()
    {
        var checkedAt = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(-5));
        var record = new SourceRecord("brand/costco", "costco", null, null, null, null, Laval.OpeningHours, null, checkedAt, false);
        var fromSource = HoursRow.Of(new HoursMatch(1, record, StationHoursEntry.ByBrand, null, true));
        var stored = fromSource with { SourceCheckedAt = checkedAt.ToUniversalTime() };

        var changes = HoursDiff.Compute(new Dictionary<long, HoursRow> { [1] = stored }, new Dictionary<long, HoursRow> { [1] = fromSource });

        Assert.Equal(0, changes.Count);
        Assert.Equal(TimeSpan.Zero, fromSource.SourceCheckedAt!.Value.Offset);
    }

    [Fact]
    public void A_row_copied_to_an_entry_reads_back_the_same()
    {
        var entry = new StationHoursEntry { Source = "costco", SourceRef = "", MatchedBy = "" };

        Laval.CopyTo(entry);

        Assert.Equal(Laval, HoursRow.Of(entry));
    }
}
