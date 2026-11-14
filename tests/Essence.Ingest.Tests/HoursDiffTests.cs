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
    public void An_element_matched_by_location_is_stored_without_its_hours_but_with_where_to_edit_it()
    {
        var station = new MatchStation(1, "key", "nutrinor-energies", 48.4, -71.1, "100", "G7J3C5");
        var sonic = new SourceRecord("way/1", "sonic", 48.40018, -71.1, null, null, "Mo-Su 06:00-23:00",
            OverpassSource.ElementLink + "way/1", new DateTimeOffset(2025, 6, 1, 4, 0, 0, TimeSpan.Zero), false);
        var match = Assert.Single(HoursMatcher.Match([sonic], [station], 150, 50).Matches);
        var entry = new StationHoursEntry { Source = OverpassSource.SourceName, SourceRef = "", MatchedBy = "" };

        HoursRow.Of(match).CopyTo(entry);

        Assert.Null(entry.OpeningHours);
        Assert.Null(entry.SourceCheckedAt);
        Assert.Equal(
            ("way/1", "https://www.openstreetmap.org/way/1", StationHoursEntry.ByLocation, 20, false, false),
            (entry.SourceRef, entry.SourceUrl, entry.MatchedBy, entry.MatchDistanceM!.Value, entry.Confirmed, entry.TemporarilyClosed));
    }

    [Fact]
    public void A_row_copied_to_an_entry_reads_back_the_same()
    {
        var entry = new StationHoursEntry { Source = "costco", SourceRef = "", MatchedBy = "" };

        Laval.CopyTo(entry);

        Assert.Equal(Laval, HoursRow.Of(entry));
    }
}
