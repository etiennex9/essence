using System.Globalization;
using System.Text;
using Essence.Data;

namespace Essence.Ingest.Tests;

/// <summary>
/// The reviewed position corrections (<c>curated-positions.json</c>, in Essence.Data, read by the API and the ingest
/// alike): when one applies, when it expires by itself, and what's refused in the file.
/// </summary>
public sealed class PositionCorrectionsTests
{
    // La station Mansfield, 281 rue Principale: the Régie's point and where OpenStreetMap's Stinson and the address are.
    private const string Mansfield = "281 rue principale, mansfield-et-pontefract|J0X1R0";
    private const double FeedLatitude = 45.85927, FeedLongitude = -76.739588;
    private const double Latitude = 45.853863, Longitude = -76.736303;

    /// <summary>About 1 m of latitude, in degrees.</summary>
    private const double Metre = 1 / 111_195.0;

    private static PositionCorrections File(string corrections) =>
        PositionCorrections.Parse(Encoding.UTF8.GetBytes($$"""{"corrections": [{{corrections}}]}"""));

    private static string Entry(
        string naturalKey = Mansfield, double latitude = Latitude, double longitude = Longitude, double feedLatitude = FeedLatitude,
        double feedLongitude = FeedLongitude, string reviewed = "\"2026-11-13\"", string note = "\"Two positions agree.\"") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"naturalKey": "{{naturalKey}}", "latitude": {{latitude}}, "longitude": {{longitude}}, "feedLatitude": {{feedLatitude}}, "feedLongitude": {{feedLongitude}}, "reviewed": {{reviewed}}, "note": {{note}}}""");

    [Fact]
    public void A_correction_applies_while_the_Régie_s_point_is_the_one_reviewed_within_50_m()
    {
        var corrections = File(Entry());
        var correction = Assert.Single(corrections.All);

        Assert.Empty(corrections.Warnings);
        Assert.Equal(new PositionCorrection(Mansfield, Latitude, Longitude, FeedLatitude, FeedLongitude, new DateOnly(2026, 11, 13), "Two positions agree."), correction);
        Assert.True(correction.AppliesTo(FeedLatitude, FeedLongitude));
        Assert.True(correction.AppliesTo(FeedLatitude + 49 * Metre, FeedLongitude));
        Assert.Equal((Latitude, Longitude), corrections.PointOf(Mansfield, FeedLatitude, FeedLongitude));
        Assert.Same(correction, corrections.AppliedTo(Mansfield, FeedLatitude, FeedLongitude));
        Assert.Equal(PositionCorrectionState.Applied, PositionCorrections.StateOf(correction, (FeedLatitude, FeedLongitude)));
    }

    [Fact]
    public void It_expires_when_the_Régie_changes_its_point_and_the_feed_s_point_is_used_again()
    {
        var corrections = File(Entry());
        var correction = corrections.All[0];
        var moved = FeedLatitude + 51 * Metre; // North, away from the corrected point.

        Assert.False(correction.AppliesTo(moved, FeedLongitude));
        Assert.Null(corrections.AppliedTo(Mansfield, moved, FeedLongitude));
        Assert.Equal((moved, FeedLongitude), corrections.PointOf(Mansfield, moved, FeedLongitude));
        Assert.Equal(PositionCorrectionState.Expired, PositionCorrections.StateOf(correction, (moved, FeedLongitude)));
    }

    [Fact]
    public void It_expires_when_the_Régie_fixes_its_point_to_within_50_m_of_the_corrected_one()
    {
        // A correction recorded 60 m from the feed's point: the Régie moving its point 20 m towards it is still the one
        // reviewed, but 40 m from the corrected point it's the fix.
        var corrections = File(Entry(latitude: FeedLatitude - 60 * Metre, longitude: FeedLongitude));
        var correction = corrections.All[0];

        Assert.True(correction.AppliesTo(FeedLatitude - 5 * Metre, FeedLongitude));
        Assert.False(correction.AppliesTo(FeedLatitude - 20 * Metre, FeedLongitude));
        Assert.Equal(PositionCorrectionState.Expired, PositionCorrections.StateOf(correction, (FeedLatitude - 20 * Metre, FeedLongitude)));
    }

    [Fact]
    public void A_station_whose_address_or_postal_code_changed_no_longer_gets_it()
    {
        var corrections = File(Entry());
        const string newKey = "281 rue principale, mansfield-et-pontefract|J0X1V0";

        Assert.Null(corrections.Find(newKey));
        Assert.Null(corrections.AppliedTo(newKey, FeedLatitude, FeedLongitude));
        Assert.Equal((FeedLatitude, FeedLongitude), corrections.PointOf(newKey, FeedLatitude, FeedLongitude));
        Assert.Equal(PositionCorrectionState.StationGone, PositionCorrections.StateOf(corrections.All[0], null));
    }

    [Fact]
    public void A_station_without_a_point_gets_none()
    {
        var corrections = File(Entry());

        Assert.False(corrections.All[0].AppliesTo(null, null));
        Assert.Equal(((double?)null, (double?)null), corrections.PointOf(Mansfield, null, null));
        Assert.Equal(PositionCorrectionState.Expired, PositionCorrections.StateOf(corrections.All[0], (null, null)));
    }

    [Theory]
    [InlineData("""{"latitude": 45.85, "longitude": -76.73, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "2026-11-13", "note": "x"}""", "no natural key")]
    [InlineData("""{"naturalKey": "281 rue principale", "latitude": 45.85, "longitude": -76.73, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "2026-11-13", "note": "x"}""", "no natural key")]
    [InlineData("""{"naturalKey": "a|B", "longitude": -76.73, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "2026-11-13", "note": "x"}""", "in Québec")]
    [InlineData("""{"naturalKey": "a|B", "latitude": -76.73, "longitude": 45.85, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "2026-11-13", "note": "x"}""", "in Québec")]
    [InlineData("""{"naturalKey": "a|B", "latitude": 45.85, "longitude": -76.73, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "13/11/2026", "note": "x"}""", "reviewed")]
    [InlineData("""{"naturalKey": "a|B", "latitude": 45.85, "longitude": -76.73, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "2026-11-13", "note": " "}""", "note")]
    [InlineData("""{"naturalKey": "a|B", "latitude": 45.8600, "longitude": -76.7400, "feedLatitude": 45.86, "feedLongitude": -76.74, "reviewed": "2026-11-13", "note": "x"}""", "would never apply")]
    [InlineData("null", "is empty")]
    public void An_entry_that_can_t_be_read_is_left_out_with_a_warning_and_the_others_apply(string entry, string warning)
    {
        var corrections = File(entry + ", " + Entry());

        Assert.Equal(Mansfield, Assert.Single(corrections.All).NaturalKey);
        Assert.Contains(warning, Assert.Single(corrections.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_correction_of_a_station_is_left_out()
    {
        var corrections = File(Entry() + ", " + Entry(latitude: 45.85));

        Assert.Equal(Latitude, Assert.Single(corrections.All).Latitude);
        Assert.Contains("the second for that station", Assert.Single(corrections.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_isn_t_JSON_leaves_none_and_says_so()
    {
        var corrections = PositionCorrections.Parse(Encoding.UTF8.GetBytes("{\"corrections\": [ {\"naturalKey\": "));

        Assert.Empty(corrections.All);
        Assert.Contains("aren't valid JSON", Assert.Single(corrections.Warnings), StringComparison.Ordinal);
        Assert.Empty(PositionCorrections.None.All);
    }

    [Fact]
    public void The_file_shipped_is_read_whole_and_each_correction_applies_to_the_point_it_was_reviewed_at()
    {
        var corrections = PositionCorrections.Embedded;

        Assert.Empty(corrections.Warnings);
        Assert.Equal(corrections.All.Count, PositionCorrections.Parse(PositionCorrections.EmbeddedBytes()).All.Count);
        Assert.All(corrections.All, c =>
        {
            Assert.True(c.AppliesTo(c.FeedLatitude, c.FeedLongitude), c.NaturalKey);
            Assert.Equal(c.NaturalKey, c.NaturalKey.Trim());
            Assert.DoesNotContain("google", c.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("street view", c.Note, StringComparison.OrdinalIgnoreCase);
        });
    }
}
