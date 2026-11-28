using System.Text.Json.Nodes;
using Essence.Hours;
using Essence.Ingest.Hours.Plugins;

namespace Essence.Ingest.Tests;

/// <summary>The hours' shapes the brands' locators and open data give, each read into a canonical rule.</summary>
public sealed class GenericHoursReaderTests
{
    private static readonly TimeRange[] NoMarker = [];

    private static HoursRead Read(string json, params TimeRange[] unknown) => GenericHoursReader.Read(JsonNode.Parse(json), unknown);

    private static void AssertRule(string expected, string json, params TimeRange[] unknown)
    {
        var read = Read(json, unknown);
        Assert.Equal((HoursReadOutcome.Rule, expected, (string?)null), (read.Outcome, read.Rule, read.Error));
    }

    private static string Days(Func<string, string> entry) =>
        "[" + string.Join(",", new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }.Select(entry)) + "]";

    [Fact]
    public void A_list_of_days_with_their_open_and_close_times_journie_s()
    {
        AssertRule("Mo-Su 06:00-22:00", Days(d => $$"""{"dayOfWeek": "{{d}}", "openTime": "06:00", "closeTime": "22:00"}"""));
        AssertRule("24/7", Days(d => $$"""{"dayOfWeek": "{{d}}", "openTime": "00:00", "closeTime": "23:59"}"""));
    }

    [Fact]
    public void An_object_keyed_by_day_with_an_always_open_flag_Circle_K_s()
    {
        AssertRule("Mo-Su 05:00-23:00", "{" + string.Join(",", new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }
            .Select(d => $$"""  "{{d}}": {"open": "05:00", "close": "23:00"}""")) + """, "alwaysOpen": false}""");
        AssertRule("24/7", """{"alwaysOpen": true}""");
        Assert.Equal(HoursReadOutcome.None, Read("""{"alwaysOpen": false}""").Outcome);
    }

    [Fact]
    public void Lines_of_days_and_times_with_a_code_Costco_s()
    {
        AssertRule("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", """
            [{"title": "Mon-Fri.", "code": "open", "time": "6:00am - 9:30pm"}, {"title": "Sat-Sun.", "code": "open", "time": "6:00am - 8:30pm"}]
            """);
    }

    [Fact]
    public void An_object_keyed_by_ranges_of_days_in_French()
    {
        AssertRule("Mo-Fr 06:00-22:00; Sa 07:00-21:00", """{"Lun-Ven": "6 h à 22 h", "Samedi": "7 h à 21 h", "Dimanche": "Fermé"}""");
    }

    [Fact]
    public void Google_s_periods_whose_days_left_out_are_closed()
    {
        AssertRule("Mo-Fr 06:00-22:00; Sa 22:00-02:00", """
            {"periods": [
              {"open": {"day": 1, "time": "0600"}, "close": {"day": 1, "time": "2200"}}, {"open": {"day": 2, "time": "0600"}, "close": {"day": 2, "time": "2200"}},
              {"open": {"day": 3, "time": "0600"}, "close": {"day": 3, "time": "2200"}}, {"open": {"day": 4, "time": "0600"}, "close": {"day": 4, "time": "2200"}},
              {"open": {"day": 5, "time": "0600"}, "close": {"day": 5, "time": "2200"}}, {"open": {"day": 6, "time": "2200"}, "close": {"day": 0, "time": "0200"}}
            ]}
            """);
        AssertRule("24/7", """{"periods": [{"open": {"day": 0, "time": "0000"}}]}""");
    }

    [Fact]
    public void Schema_org_s_opening_hours_as_rules_or_as_specifications()
    {
        AssertRule("Mo-Fr 05:30-23:00; Sa-Su 07:00-23:00", """["Mo-Fr 05:30-23:00", "Sa-Su 07:00-23:00"]""");
        AssertRule("Mo-Su 06:00-22:00", """[{"@type": "OpeningHoursSpecification", "dayOfWeek": ["https://schema.org/Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], "opens": "06:00:00", "closes": "22:00:00"}]""");

        // schema.org closes a day with 00:00 to 00:00, where others mean all day: which is meant can't be told.
        var read = Read("""[{"dayOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], "opens": "00:00", "closes": "00:00"}]""");
        Assert.Equal(HoursReadOutcome.Refused, read.Outcome);
    }

    [Fact]
    public void A_day_given_twice_is_its_split_shifts_unless_they_overlap()
    {
        var split = Days(d => $$"""{"day": "{{d}}", "open": "06:00", "close": "12:00"}""").TrimEnd(']')
            + "," + Days(d => $$"""{"day": "{{d}}", "open": "13:00", "close": "21:00"}""").TrimStart('[');
        AssertRule("Mo-Su 06:00-12:00,13:00-21:00", split);

        var twice = Days(d => $$"""{"day": "{{d}}", "open": "06:00", "close": "22:00"}""").TrimEnd(']')
            + ",{\"day\": \"Monday\", \"open\": \"06:00\", \"close\": \"22:00\"}]";
        Assert.Contains("overlap", Read(twice).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_that_means_the_source_doesn_t_say_refuses_the_hours()
    {
        var json = Days(d => d == "Sunday"
            ? $$"""{"dayOfWeek": "{{d}}", "openTime": "00:00", "closeTime": "12:00"}"""
            : $$"""{"dayOfWeek": "{{d}}", "openTime": "06:00", "closeTime": "22:00"}""");

        AssertRule("Mo-Sa 06:00-22:00; Su 00:00-12:00", json);
        Assert.Equal(HoursReadOutcome.Refused, Read(json, new TimeRange(0, 720)).Outcome);
    }

    [Fact]
    public void Days_named_with_holidays_keep_their_hours_and_the_holidays_alone_are_skipped()
    {
        AssertRule("Mo-Sa 06:00-22:00; Su 08:00-20:00", """{"Lun-Sam": "6h-22h", "Dimanche et fériés": "8h-20h"}""");
        AssertRule("Mo-Su 06:00-22:00", """{"Lun-Dim": "6h-22h", "Jours fériés": "fermé"}""");
        AssertRule("Mo-Sa 06:00-22:00; Su 08:00-20:00", """
            [{"dayOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"], "opens": "06:00", "closes": "22:00"},
             {"dayOfWeek": ["Sunday", "https://schema.org/PublicHolidays"], "opens": "08:00", "closes": "20:00"},
             {"dayOfWeek": "https://schema.org/PublicHolidays", "opens": "09:00", "closes": "17:00"}]
            """);
    }

    [Fact]
    public void A_flag_closes_a_day_only_when_it_says_so()
    {
        var monday = Days(d => d == "Monday"
            ? """{"day": "Monday", "open": "06:00", "close": "22:00", "isOpen": false}"""
            : $$"""{"day": "{{d}}", "open": "06:00", "close": "22:00", "isOpenNow": true}""");
        AssertRule("Tu-Su 06:00-22:00", monday);
        AssertRule("Tu-Su 06:00-22:00", Days(d => d == "Monday" ? """{"day": "Monday", "open": false}""" : $$"""{"day": "{{d}}", "open": "06:00", "close": "22:00"}"""));
        AssertRule("Tu-Su 06:00-22:00", Days(d => d == "Monday" ? """{"day": "Monday", "closed": true}""" : $$"""{"day": "{{d}}", "open": "06:00", "close": "22:00"}"""));
    }

    [Theory]
    [InlineData("""[{"day": "Monday", "open": 600, "close": 2200}, {"day": "Tue-Sun", "open": "06:00", "close": "22:00"}]""", "aren't understood")]
    [InlineData("""[{"day": "Monday", "open": "", "close": ""}, {"day": "Tue-Sun", "open": "06:00", "close": "22:00"}]""", "aren't understood")]
    [InlineData("""[{"day": "Monday", "open": {"hour": 6}, "close": {"hour": 22}}, {"day": "Tue-Sun", "open": "06:00", "close": "22:00"}]""", "aren't understood")]
    [InlineData("""[{"day": "Monday", "open": "06:00", "close": "22:00", "closed": "true"}, {"day": "Tue-Sun", "open": "06:00", "close": "22:00"}]""", "isn't true or false")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "isHoliday": true}]""", "\"isHoliday\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "closed": true, "open24h": true}]""", "closed and open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00"}, {"day": "Sunday", "closed": true}]""", "Sunday's hours are given twice")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "12:00"}, {"day": "Sunday", "open": "13:00", "close": "20:00"}]""", "Sunday's hours are given twice")]
    [InlineData("""[{"day": "Monday", "open": "06:00", "close": "22:00"}, {"day": "Monday", "closed": true}]""", "Monday's hours are given twice")]
    [InlineData("""{"Lun-Dim": "6h-22h", "Dimanche": "Fermé"}""", "Sunday's hours are given twice")]
    [InlineData("""{"Lun-Dim": "6h-22h", "Dimanche et fériés": "fermé"}""", "Sunday's hours are given twice")]
    [InlineData("""{"carWash": "Lun-Dim 8h-20h"}""", "\"carWash\" isn't a day")]
    [InlineData("""{"Lun-Dim": "6h-22h", "open24h": "yes"}""", "isn't true or false")]
    [InlineData("""[{"day": "Mon-Sun", "hours": [{"open": "06:00", "close": "12:00"}, {"day": "Sunday", "open": "13:00", "close": "14:00"}]}]""", "name other days")]
    [InlineData("""{"Lun-Dim": {"day": "Dimanche", "open": "06:00", "close": "22:00"}}""", "name other days")]
    [InlineData("""{"openingHoursSpecification": [{"dayOfWeek": "Monday", "opens": "06:00", "closes": "22:00"}], "sunday": "Fermé"}""", "\"sunday\" is given beside its list")]
    [InlineData("""{"Lun-Dim": "6h-22h", "Dimanche": null}""", "Dimanche's hours are null")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "dayClosed": "true"}]""", "\"dayClosed\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "isHoliday": "true"}]""", "\"isHoliday\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "isClosedDay": 1}]""", "\"isClosedDay\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "closed": {"value": true}}]""", "\"closed\" isn't true or false")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "isOpen": {"value": false}}]""", "\"isOpen\" isn't true or false")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "isOpen": [false]}]""", "\"isOpen\" isn't true or false")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "closed": true, "isOpen": true}]""", "closed and open")]
    [InlineData("""{"periods": [{"open": {"day": 1, "time": "0600"}, "close": {"day": 1, "time": "2200"}, "isOpen": false}]}""", "a period's field \"isOpen\"")]
    [InlineData("""{"periods": [{"open": {"day": 1, "time": "1:00"}, "close": {"day": 1, "time": "6:00 pm"}}]}""", "12-hour clock")]
    [InlineData("""[{"day": "Mon-Sun", "hours": [{"open": "06:00", "close": "22:00"}, {"sunday": "closed"}]}]""", "\"sunday\" names a day")]
    [InlineData("""{"Lun-Dim": [{"open": "06:00", "close": "22:00"}, {"dimanche": "Fermé"}]}""", "\"dimanche\" names a day")]
    [InlineData("""[{"day": "Mon-Sun", "hours": [{"open": "06:00", "close": "22:00"}, {"day": 0, "closed": true}]}]""", "name other days")]
    [InlineData("""[{"day": "Mon-Sun", "hours": [{"open": "06:00", "close": "22:00"}, {"day": "Sundays only", "closed": true}]}]""", "name other days")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "sunday": "closed"}]""", "\"sunday\" names a day")]
    [InlineData("""{"openingHoursSpecification": [{"dayOfWeek": "Monday", "opens": "06:00", "closes": "22:00"}], "sundayClosed": true}""", "\"sundayClosed\" is given beside")]
    [InlineData("""{"openingHoursSpecification": [{"dayOfWeek": "Monday", "opens": "06:00", "closes": "22:00"}], "closedOn": "Sunday"}""", "\"closedOn\" is given beside")]
    [InlineData("""{"Lun-Dim": ["6h-22h", "Fermé"]}""", "closed and open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "dayOff": true}]""", "\"dayOff\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "available": false}]""", "\"available\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "active": false}]""", "\"active\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "enabled": false}]""", "\"enabled\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "status": "closed"}]""", "doesn't say it's open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "statut": "fermé"}]""", "doesn't say it's open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "state": "CLOSED"}]""", "doesn't say it's open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "etat": "ferme"}]""", "doesn't say it's open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "status": {"code": "closed"}}]""", "doesn't say it's open")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "note": "Fermé"}]""", "\"note\" isn't understood")]
    [InlineData("""[{"day": "Mon-Sun", "open": "06:00", "close": "22:00", "isOpen": false, "closed": false}]""", "closed and open")]
    [InlineData("""{"periods": [{"open": {"day": 1, "time": "0600", "isOpen": false}, "close": {"day": 1, "time": "2200"}}]}""", "has a field \"isOpen\"")]
    [InlineData("""{"periods": [{"open": {"day": 1, "time": "0600"}, "close": {"day": 1, "time": "2200", "closed": true}}]}""", "has a field \"closed\"")]
    public void Hours_that_could_be_misread_are_refused_with_the_reason(string json, string reason)
    {
        var read = Read(json);
        Assert.Equal(HoursReadOutcome.Refused, read.Outcome);
        Assert.Contains(reason, read.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_s_hours_may_name_the_same_day_again_and_flags_of_the_moment_say_nothing()
    {
        AssertRule("Mo-Su 06:00-22:00", "{" + string.Join(",", new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }
            .Select(d => $$"""  "{{d}}": {"day": "{{d}}", "open": "06:00", "close": "22:00", "isToday": false}""")) + "}");
        AssertRule("Mo-Su 06:00-22:00", Days(d => $$"""{"day": "{{d}}", "open": "06:00", "close": "22:00", "isOpen": true, "openNow": false}"""));
        AssertRule("Mo-Su 06:00-22:00", Days(d => $$"""{"day": "{{d}}", "open": "06:00", "close": "22:00", "status": "open", "sortOrder": 3, "label": "Matin"}"""));
        AssertRule("Mo-Su 06:00-22:00", """{"periods": [{"open": {"day": 0, "time": "0600"}, "close": {"day": 0, "time": "2200"}}, {"open": {"day": 1, "time": "0600"}, "close": {"day": 1, "time": "2200"}}, {"open": {"day": 2, "time": "0600"}, "close": {"day": 2, "time": "2200"}}, {"open": {"day": 3, "time": "0600"}, "close": {"day": 3, "time": "2200"}}, {"open": {"day": 4, "time": "0600"}, "close": {"day": 4, "time": "2200"}}, {"open": {"day": 5, "time": "0600"}, "close": {"day": 5, "time": "2200"}}, {"open": {"day": 6, "time": "0600"}, "close": {"day": 6, "time": "2200"}}], "open_now": true}""");
    }

    [Fact]
    public void A_field_that_holds_the_week_is_looked_into_another_department_s_never()
    {
        AssertRule("Mo-Su 06:00-22:00", """{"regular": {"Lun-Dim": "6h-22h"}}""");
        AssertRule("Mo-Su 06:00-22:00", """{"weeklySchedule": {"Lun-Dim": "6h-22h"}}""");
        Assert.Equal(HoursReadOutcome.Refused, Read("""{"storeSchedule": {"Lun-Dim": "6h-22h"}}""").Outcome);
        Assert.Equal(HoursReadOutcome.Refused, Read(Days(d => $$"""{"day": "{{d}}", "carWash": "8h-20h"}""")).Outcome);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Nothing_is_no_hours(string json) => Assert.Equal(HoursReadOutcome.None, Read(json).Outcome);

    [Theory]
    [InlineData("42")]
    [InlineData("""{"lundi": "6h-22h", "remarque": "été"}""")]
    [InlineData("""[{"dayOfWeek": 1, "open": "06:00", "close": "22:00"}]""")]
    [InlineData("""[{"open": "06:00", "close": "22:00"}]""")]
    [InlineData("\"Mo-Su off\"")]
    public void What_can_t_be_read_for_sure_is_refused(string json) => Assert.Equal(HoursReadOutcome.Refused, Read(json).Outcome);
}
