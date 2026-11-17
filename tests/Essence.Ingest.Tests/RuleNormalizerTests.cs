using Essence.Hours;
using Essence.Ingest.Hours;

namespace Essence.Ingest.Tests;

public sealed class RuleNormalizerTests
{
    [Theory]
    [InlineData("Mon-Fri. ", "Mo Tu We Th Fr")]
    [InlineData("Mon.-Fri.", "Mo Tu We Th Fr")]
    [InlineData("Mon - Fri", "Mo Tu We Th Fr")]
    [InlineData("Sat. ", "Sa")]
    [InlineData("Sun.", "Su")]
    [InlineData("Mon-Sun.", "Mo Tu We Th Fr Sa Su")]
    [InlineData("Thu.", "Th")]
    [InlineData("Thurs.", "Th")]
    [InlineData("Tuesday", "Tu")]
    [InlineData("Sat-Mon", "Sa Su Mo")]
    [InlineData("SAT.–SUN.", "Sa Su")]
    public void Days_are_read_as_the_locators_write_them(string title, string expected)
    {
        Assert.True(RuleNormalizer.TryDays(title, out var days));
        Assert.Equal(expected, string.Join(" ", days.Select(d => d.ToString()[..2])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Weekdays")]
    [InlineData("Holidays")]
    [InlineData("Mon-")]
    [InlineData("Mon-Wed-Fri")]
    [InlineData("Lun.-Ven.")]
    public void Days_that_aren_t_understood_are_refused(string title)
    {
        Assert.False(RuleNormalizer.TryDays(title, out _));
    }

    [Theory]
    [InlineData("open", "6:00am - 9:30pm", "06:00-21:30")]
    [InlineData(null, "6:00am - 9:30pm", "06:00-21:30")]
    [InlineData("open", "6am-9:30 p.m.", "06:00-21:30")]
    [InlineData("open", "6:00 AM – 8:30 PM", "06:00-20:30")]
    [InlineData("open", "12:00am - 12:00am", "00:00-24:00")]
    [InlineData("open", "6:00am - 12:00am", "06:00-24:00")]
    [InlineData("open", "12:00pm - 5:00pm", "12:00-17:00")]
    [InlineData("open", "9:00pm - 2:00am", "21:00-02:00")]
    [InlineData("open", "6:00am - 12:00pm, 1:00pm - 9:00pm", "06:00-12:00,13:00-21:00")]
    [InlineData(null, "closed", "off")]
    [InlineData("closed", "Closed", "off")]
    [InlineData("closed", "", "off")]
    public void Times_are_read_as_the_locators_write_them(string? code, string time, string expected)
    {
        Assert.True(RuleNormalizer.TryTimes(code, time, out var ranges));
        Assert.Equal(expected, ranges.Count == 0 ? "off" : string.Join(",", ranges));
    }

    [Theory]
    [InlineData("open", "6:00am - noon")]
    [InlineData("open", "06:00 - 21:30")]
    [InlineData("open", "13:00pm - 2:00pm")]
    [InlineData("open", "6:00am")]
    [InlineData("open", "6:00am - 6:00am")]
    [InlineData("open", "Open 24 hours")]
    [InlineData("open", "sunrise - sunset")]
    [InlineData("open", "closed")]
    [InlineData("closed", "6:00am - 9:30pm")]
    [InlineData("holiday", "6:00am - 9:30pm")]
    [InlineData("open", "")]
    public void Times_that_aren_t_understood_or_contradict_the_code_are_refused(string? code, string time)
    {
        Assert.False(RuleNormalizer.TryTimes(code, time, out _));
    }

    [Fact]
    public void Costco_s_week_is_its_canonical_rule()
    {
        var rule = RuleNormalizer.Week(
            [new("Mon-Fri. ", "open", "6:00am - 9:30pm"), new("Sat. ", "open", "6:00am - 8:30pm"), new("Sun. ", "open", "6:00am - 8:30pm")],
            out var error);

        Assert.Null(error);
        Assert.Equal("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30", rule);
    }

    [Fact]
    public void A_week_with_a_closed_day_and_one_past_midnight_reads_back_the_same()
    {
        var rule = RuleNormalizer.Week(
            [new("Mon-Thu.", "open", "6:00am - 11:00pm"), new("Fri-Sat.", "open", "6:00am - 2:00am"), new("Sun.", "closed", "closed")],
            out _);

        Assert.Equal("Mo-Th 06:00-23:00; Fr-Sa 06:00-02:00", rule);
        Assert.Equal(rule, OpeningHours.Parse(rule!).ToRule());
    }

    [Fact]
    public void A_week_missing_a_day_is_refused_rather_than_taken_as_closed()
    {
        var rule = RuleNormalizer.Week([new("Mon-Fri. ", "open", "6:00am - 9:30pm"), new("Sat. ", "open", "6:00am - 8:30pm")], out var error);

        Assert.Null(rule);
        Assert.Equal("Sunday's hours aren't given", error);
    }

    [Fact]
    public void A_day_given_twice_is_refused()
    {
        var rule = RuleNormalizer.Week(
            [new("Mon-Sun.", "open", "6:00am - 9:30pm"), new("Sat. ", "open", "6:00am - 8:30pm")], out var error);

        Assert.Null(rule);
        Assert.Equal("Saturday's hours are given twice", error);
    }

    [Fact]
    public void A_week_with_a_line_not_understood_says_which()
    {
        var rule = RuleNormalizer.Week([new("Holidays", "open", "6:00am - 9:30pm")], out var error);

        Assert.Null(rule);
        Assert.Equal("the days \"Holidays\" aren't understood", error);
    }
}
