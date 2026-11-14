using Essence.Contracts;
using Essence.Hours;
using Essence.UI.Services;

namespace Essence.UI.Tests;

/// <summary>
/// The hours editor's week (HoursEditor.razor): what it starts from, the rule it saves (OpeningHours.FromDays, then ToRule),
/// and whether the correction may go to OpenStreetMap, with the station's brand or not.
/// </summary>
public class HoursDraftTests
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";

    [Fact]
    public void Costco_s_week_comes_back_as_it_was()
    {
        var draft = HoursDraft.From(OpeningHours.Parse(CostcoRule));

        Assert.False(draft.AlwaysOpen);
        Assert.Equal(DayOfWeek.Monday, draft.Days[0].Day);
        Assert.Equal(DayOfWeek.Sunday, draft.Days[^1].Day);
        Assert.Equal(("06:00", "21:30", false), (draft.Days[0].Opens, draft.Days[0].Closes, draft.Days[0].Closed));
        Assert.Equal(("06:00", "20:30"), (draft.Days[6].Opens, draft.Days[6].Closes));
        Assert.Equal(CostcoRule, Build(draft));
    }

    [Theory]
    [InlineData("Mo-Sa 07:00-23:00")]
    [InlineData("Mo-Fr 22:00-02:00; Sa-Su 10:00-20:00")]
    [InlineData("Mo-Th 06:00-24:00; Fr-Sa 00:00-24:00; Su 08:00-22:00")]
    [InlineData("Mo-Su 06:00-22:00; PH off")]
    [InlineData("Mo-Su off")]
    [InlineData("24/7")]
    [InlineData("Mo-Fr 06:00-12:00,13:00-21:00; Sa 08:00-12:00,13:00-17:00,18:00-22:00")]
    [InlineData("Mo-Sa 06:00-22:00; Su 08:00-12:00,20:00-02:00; PH 10:00-14:00")]
    public void Every_week_left_as_it_is_saves_as_the_same_rule(string rule) =>
        Assert.Equal(OpeningHours.Parse(rule).ToRule(), Build(HoursDraft.From(OpeningHours.Parse(rule))));

    [Fact]
    public void Days_open_in_several_stretches_keep_them_unless_they_re_changed()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Fr 06:00-12:00,13:00-21:00; Sa-Su 08:00-20:00"));
        Assert.Equal(("06:00", "21:00"), (draft.Days[0].Opens, draft.Days[0].Closes));
        Assert.True(draft.KeepsStretches);

        draft.SetCloses(DayOfWeek.Sunday, "18:00");
        Assert.Equal("Mo-Fr 06:00-12:00,13:00-21:00; Sa 08:00-20:00; Su 08:00-18:00", Build(draft));

        draft.SetCloses(DayOfWeek.Monday, "22:00");
        Assert.Equal("Mo 06:00-22:00; Tu-Fr 06:00-12:00,13:00-21:00; Sa 08:00-20:00; Su 08:00-18:00", Build(draft));

        draft.SetCloses(DayOfWeek.Monday, "21:00");
        Assert.StartsWith("Mo-Fr 06:00-12:00,13:00-21:00;", Build(draft));
    }

    [Fact]
    public void A_split_day_changed_is_the_one_stretch_it_says_and_closing_it_closes_it()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Su 06:00-12:00,13:00-21:00"));

        foreach (var day in draft.Days.Take(5))
        {
            draft.SetOpens(day.Day, "07:00");
        }

        draft.Days[5].Closed = true;

        Assert.True(draft.KeepsStretches);
        Assert.Equal("Mo-Fr 07:00-21:00; Su 06:00-12:00,13:00-21:00", Build(draft));
        draft.Days[6].Closed = true;
        Assert.False(draft.KeepsStretches);
    }

    [Fact]
    public void A_split_week_unchanged_from_OpenStreetMap_starts_unticked()
    {
        const string rule = "Mo-Fr 06:00-12:00,13:00-21:00; Sa-Su 08:00-20:00";
        var draft = HoursDraft.From(OpeningHours.Parse(rule));
        Assert.True(draft.TryBuild(out var hours, out _));

        Assert.Equal((true, false), HoursDraft.OsmChoice(Hours(rule, "osm"), hours, surveyed: false));
    }

    [Fact]
    public void A_closed_day_is_left_out_of_the_rule()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Su 06:00-22:00"));

        draft.Days[6].Closed = true;

        Assert.Equal("Mo-Sa 06:00-22:00", Build(draft));
    }

    [Fact]
    public void A_closing_time_before_the_opening_is_the_next_day_s_and_midnight_ends_the_day()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Su 06:00-22:00"));

        draft.SetCloses(DayOfWeek.Friday, "02:00");
        draft.SetCloses(DayOfWeek.Saturday, "00:00");

        Assert.Equal("Mo-Th 06:00-22:00; Fr 06:00-02:00; Sa 06:00-24:00; Su 06:00-22:00", Build(draft));
        Assert.Equal(new TimeRange(360, 1560), OpeningHours.Parse(Build(draft)).On(DayOfWeek.Friday).Single());
    }

    [Fact]
    public void The_same_time_twice_is_all_day()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Su 06:00-22:00"));

        draft.SetOpens(DayOfWeek.Saturday, "00:00");
        draft.SetCloses(DayOfWeek.Saturday, "00:00");

        Assert.Equal("Mo-Fr 06:00-22:00; Sa 00:00-24:00; Su 06:00-22:00", Build(draft));
    }

    [Fact]
    public void Open_24_hours_is_24_7_whatever_the_days_say()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Su 06:00-22:00; PH off"));

        draft.AlwaysOpen = true;
        draft.Days[0].Closed = true;

        Assert.Equal("24/7", Build(draft));
    }

    [Fact]
    public void Unknown_hours_start_blank_and_a_week_typed_from_Monday_is_typed_once()
    {
        var draft = HoursDraft.From(null);
        Assert.All(draft.Days, d => Assert.Equal(("", "", false), (d.Opens, d.Closes, d.Closed)));

        draft.Days[6].Closed = true;
        draft.SetOpens(DayOfWeek.Monday, "07:00");
        draft.SetCloses(DayOfWeek.Monday, "21:00");
        draft.SetCloses(DayOfWeek.Saturday, "18:00");

        Assert.Equal("Mo-Fr 07:00-21:00; Sa 07:00-18:00", Build(draft));
    }

    [Fact]
    public void Days_filled_from_Monday_follow_it_as_its_time_is_typed()
    {
        // A desktop browser's time box says each keystroke that makes a time: 2, 1, 3 is 21:03, then 0 is 21:30.
        var draft = HoursDraft.From(null);
        draft.Days[6].Closed = true;

        draft.SetOpens(DayOfWeek.Monday, "06:03");
        draft.SetOpens(DayOfWeek.Monday, "06:30");
        draft.SetCloses(DayOfWeek.Monday, "21:03");
        draft.SetCloses(DayOfWeek.Monday, "21:30");

        Assert.Equal("Mo-Sa 06:30-21:30", Build(draft));
        Assert.All(draft.Days.Skip(1).Take(5), d => Assert.Equal((DayOfWeek.Monday, DayOfWeek.Monday), (d.OpensFrom, d.ClosesFrom)));
    }

    [Fact]
    public void A_day_whose_time_is_typed_stops_following_and_the_others_still_do()
    {
        var draft = HoursDraft.From(null);
        draft.SetOpens(DayOfWeek.Monday, "07:00");
        draft.SetCloses(DayOfWeek.Monday, "21:00");

        draft.SetCloses(DayOfWeek.Tuesday, "22:03");
        draft.SetCloses(DayOfWeek.Tuesday, "22:30");
        draft.SetCloses(DayOfWeek.Monday, "20:00");

        Assert.Null(draft.Days[1].ClosesFrom);
        Assert.Equal("Mo 07:00-20:00; Tu 07:00-22:30; We-Su 07:00-20:00", Build(draft));
    }

    [Fact]
    public void Times_typed_on_a_day_don_t_replace_the_days_after_it_that_are_set()
    {
        var draft = HoursDraft.From(OpeningHours.Parse(CostcoRule));

        draft.SetOpens(DayOfWeek.Monday, "05:00");

        Assert.Equal("Mo 05:00-21:30; Tu-Fr 06:00-21:30; Sa-Su 06:00-20:30", Build(draft));
    }

    [Fact]
    public void A_day_without_both_times_isn_t_saved_and_says_which()
    {
        var draft = HoursDraft.From(null);
        draft.SetOpens(DayOfWeek.Monday, "07:00");
        draft.SetCloses(DayOfWeek.Monday, "21:00");
        draft.Days[3].Closes = "";

        Assert.False(draft.TryBuild(out var hours, out var day));
        Assert.Null(hours);
        Assert.Equal(DayOfWeek.Thursday, day);
    }

    [Fact]
    public void Times_with_seconds_are_read_too()
    {
        var draft = HoursDraft.From(OpeningHours.Parse("Mo-Su 06:00-22:00"));

        draft.SetOpens(DayOfWeek.Monday, "05:30:00");

        Assert.StartsWith("Mo 05:30-22:00;", Build(draft));
    }

    [Fact]
    public void Hours_the_user_changed_go_to_OpenStreetMap_ticked()
    {
        var edited = OpeningHours.Parse("Mo-Fr 06:00-22:00; Sa-Su 06:00-20:30");

        Assert.Equal((true, true), HoursDraft.OsmChoice(Hours(CostcoRule, "costco"), edited, surveyed: false));
        Assert.Equal((true, true), HoursDraft.OsmChoice(null, edited, surveyed: false));
    }

    [Theory]
    [InlineData("costco")]
    [InlineData("curated")]
    [InlineData("circlek")]
    [InlineData("ultramar")]
    [InlineData("shell")]
    [InlineData("a-source-from-a-newer-api")]
    public void A_brand_s_hours_unchanged_are_never_pushed_on_their_own_only_once_checked_at_the_station(string source)
    {
        var edited = OpeningHours.Parse("Mo-Fr 06:00-21:30;Sa-Su 06:00-20:30", lenient: true);

        Assert.Equal((false, false), HoursDraft.OsmChoice(Hours(CostcoRule, source), edited, surveyed: false));
        Assert.Equal((true, true), HoursDraft.OsmChoice(Hours(CostcoRule, source), edited, surveyed: true));
    }

    [Fact]
    public void OpenStreetMap_s_own_hours_unchanged_start_unticked_unless_checked()
    {
        var edited = OpeningHours.Parse(CostcoRule);

        Assert.Equal((true, false), HoursDraft.OsmChoice(Hours(CostcoRule, "osm"), edited, surveyed: false));
        Assert.Equal((true, true), HoursDraft.OsmChoice(Hours(CostcoRule, "osm"), edited, surveyed: true));
    }

    [Fact]
    public void The_station_s_brand_is_offered_for_an_element_matched_by_place_only_unless_OpenStreetMap_says_it_now()
    {
        var station = TestStations.Detail(1, "Nutrinor Énergies") with { OsmElement = "way/456", OsmBrandDiffers = true };

        Assert.Equal("Nutrinor Énergies", HoursDraft.BrandToOffer(station, null));
        Assert.Equal("Nutrinor Énergies", HoursDraft.BrandToOffer(station, Tags(("name", "Dépanneur Sonic"))));
        Assert.Equal("Nutrinor Énergies", HoursDraft.BrandToOffer(station, Tags(("brand", "Sonic"))));
        Assert.Equal("Nutrinor Énergies", HoursDraft.BrandToOffer(station with { Brand = " Nutrinor Énergies " }, null));
        Assert.Null(HoursDraft.BrandToOffer(station, Tags(("brand", "NUTRINOR ENERGIES"))));
    }

    [Fact]
    public void No_brand_is_offered_for_an_element_of_the_station_s_brand_an_independent_or_from_an_older_API()
    {
        var station = TestStations.Detail(1, "Nutrinor Énergies") with { OsmElement = "way/456", OsmBrandDiffers = true };

        Assert.Null(HoursDraft.BrandToOffer(station with { OsmBrandDiffers = false }, null));
        Assert.Null(HoursDraft.BrandToOffer(station with { OsmBrandDiffers = null }, null));
        Assert.Null(HoursDraft.BrandToOffer(station with { Brand = null }, Tags(("brand", "Sonic"))));
        Assert.Null(HoursDraft.BrandToOffer(station with { Brand = "  " }, null));
    }

    [Theory]
    [InlineData("Indépendant")]
    [InlineData("INDÉPENDANT")]
    [InlineData("Independent")]
    [InlineData("Aucun")]
    public void The_feed_s_word_for_an_independent_is_no_brand_to_offer(string brand)
    {
        // A garage "Indépendant" in the feed, on an element of a name of its own and no brand, matched by place.
        var station = TestStations.Detail(1, brand) with { OsmElement = "node/123", OsmBrandDiffers = true };

        Assert.Null(HoursDraft.BrandToOffer(station, Tags(("name", "Garage Untel"))));
        Assert.Null(HoursDraft.BrandToOffer(station, null));
    }

    [Fact]
    public void Setting_the_brand_starts_unticked_is_ticked_only_by_the_user_and_goes_only_with_the_hours()
    {
        Assert.Equal((true, false), HoursDraft.BrandChoice(sendsHours: true, chosen: false));
        Assert.Equal((true, true), HoursDraft.BrandChoice(sendsHours: true, chosen: true));
        Assert.Equal((false, false), HoursDraft.BrandChoice(sendsHours: false, chosen: true));
        Assert.Equal((false, false), HoursDraft.BrandChoice(sendsHours: false, chosen: false));
    }

    [Fact]
    public void A_brand_s_hours_left_as_they_were_and_unchecked_send_neither_them_nor_the_brand()
    {
        var edited = OpeningHours.Parse(CostcoRule);

        var (allowed, ticked) = HoursDraft.OsmChoice(Hours(CostcoRule, "ultramar"), edited, surveyed: false);
        Assert.Equal((false, false), HoursDraft.BrandChoice(allowed && ticked, chosen: true));

        // Checked at the station, they can go, and the brand with them, once it's ticked.
        (allowed, ticked) = HoursDraft.OsmChoice(Hours(CostcoRule, "ultramar"), edited, surveyed: true);
        Assert.Equal((true, false), HoursDraft.BrandChoice(allowed && ticked, chosen: false));
        Assert.Equal((true, true), HoursDraft.BrandChoice(allowed && ticked, chosen: true));
    }

    private static OsmElementTags Tags(params (string Key, string Value)[] tags) =>
        new(tags.Append((Key: "amenity", Value: "fuel")).ToDictionary(t => t.Key, t => t.Value));

    private static StationHours Hours(string rule, string source) => new(rule, source, Trusted: true, Confirmed: true, null, null);

    private static string Build(HoursDraft draft)
    {
        Assert.True(draft.TryBuild(out var hours, out var invalid), $"{invalid} is incomplete.");
        return hours.ToRule();
    }
}
