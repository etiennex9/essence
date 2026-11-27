using System.Globalization;
using Essence.Contracts;
using Essence.Hours;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Essence.UI.Tests;

public sealed class AreaTrendsViewTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = QuebecTime.Eastern;

    /// <summary>00:20 in Montréal, November 20 2026.</summary>
    private static readonly DateTimeOffset Night = new(2026, 11, 20, 0, 20, 0, TimeSpan.FromHours(-5));

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly AreaTrendsView _view = new(new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)));

    public AreaTrendsViewTests() => Culture("en-CA");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    private static PriceJump Jump(params NotRaisedStation[] held) =>
        new(Night.AddMinutes(-15), Night.AddMinutes(40), 116, 160, 10.6m, 151.9m, 162.9m, held, Usual: true);

    private static PriceTrends Trends(PriceJump? jump = null, DailyPattern? pattern = null) =>
        new("Régulier", Night, 10, 160, new AreaPrices(Night, 140.9m, 162.9m, 160), null, null, null, null, jump, pattern);

    [Theory]
    [InlineData("en-CA", "Prices have been going up since 12:05 a.m.: 116 of 160 stations up about 11 ¢.")]
    [InlineData("fr-CA", "Les prix montent depuis 0 h 05 : 116 stations sur 160 ont augmenté d’environ 11 ¢.")]
    public void A_rise_going_on_says_so_until_it_ends(string culture, string text)
    {
        Culture(culture);

        Assert.Equal(text, Plain(_view.JumpText(Jump(), Night, Zone)));
        Assert.Null(_view.JumpText(Jump(), Night.AddMinutes(41), Zone));
        Assert.Null(_view.JumpText(null, Night, Zone));
    }

    [Fact]
    public void A_follower_still_at_its_price_hasnt_gone_up_until_the_rise_ends_or_it_does()
    {
        var trends = Trends(Jump(new NotRaisedStation(7, 151.9m)));

        Assert.True(AreaTrendsView.HasntGoneUp(trends, 7, 151.9m, Night));
        Assert.True(AreaTrendsView.HasntGoneUp(trends, 7, 150.9m, Night));
        Assert.False(AreaTrendsView.HasntGoneUp(trends, 7, 162.9m, Night));
        Assert.False(AreaTrendsView.HasntGoneUp(trends, 8, 151.9m, Night));
        Assert.False(AreaTrendsView.HasntGoneUp(trends, 7, 151.9m, Night.AddMinutes(41)));
        Assert.Equal(Night.AddMinutes(40), AreaTrendsView.ChangesAt(trends, Night));
        Assert.Null(AreaTrendsView.ChangesAt(trends, Night.AddMinutes(41)));
    }

    [Theory]
    [InlineData("en-CA", "Here, prices usually go up around midnight and are lowest in the evening.")]
    [InlineData("fr-CA", "Ici, les prix montent habituellement vers minuit et sont au plus bas en soirée.")]
    public void The_pattern_says_when_prices_go_up_and_are_lowest(string culture, string text)
    {
        Culture(culture);

        Assert.Equal(text, _view.PatternText(new DailyPattern(new TimeOnly(0, 0), 18, 0, 28, null, 4)));
    }

    [Theory]
    [InlineData("en-CA", "Around 11:45 p.m., prices here usually go up. They’re usually lowest on Thursdays and Fridays.")]
    [InlineData("fr-CA", "Ici, les prix montent habituellement vers 23 h 45. Ils sont habituellement au plus bas le jeudi et le vendredi.")]
    public void The_cheapest_days_follow(string culture, string text)
    {
        Culture(culture);

        Assert.Equal(text, Plain(_view.PatternText(new DailyPattern(new TimeOnly(23, 45), null, null, 28, [4, 5], 6))));
    }

    [Fact]
    public void Only_days_or_nothing()
    {
        Assert.Equal("Here, prices are usually lowest on Sundays and Mondays.", _view.PatternText(new DailyPattern(null, null, null, 28, [7, 1], 6)));
        Assert.Null(_view.PatternText(new DailyPattern(null, null, null, 28, null, 2)));
        Assert.Null(_view.PatternText(null));
    }

    [Theory]
    [InlineData(18, 0, "DayPartEvening")]
    [InlineData(18, 23, "DayPartEvening")]
    [InlineData(22, 3, "DayPartNight")]
    [InlineData(6, 10, "DayPartMorning")]
    [InlineData(11, 14, "DayPartMidday")]
    [InlineData(14, 17, "DayPartAfternoon")]
    public void The_lowest_hours_are_said_by_their_part_of_the_day(int from, int to, string key) =>
        Assert.Equal(key, AreaTrendsView.DayPartKey(from, to));

    [Theory]
    [InlineData("en-CA", "Lowest in 24 h: 139.9 at Costco (until 10:55 p.m. yesterday).")]
    [InlineData("fr-CA", "Le plus bas en 24 h : 139,9 chez Costco (jusqu’à hier à 22 h 55).")]
    public void The_24_hour_low_says_until_when(string culture, string text)
    {
        Culture(culture);
        var low = new AreaLow(3, null, "Costco", 139.9m, Night.AddHours(-12), Night.AddMinutes(-85), StillNow: false);

        Assert.Equal(text, Plain(_view.LowText(low, "Costco", Night, Zone)));
        Assert.Equal("Lowest in 24 h: 139.9 at Costco, its price now.", Culture("en-CA", () => _view.LowText(low with { To = null, StillNow = true }, "Costco", Night, Zone)));
        Assert.Equal("Lowest in 24 h: 139.9 at Costco.", Culture("en-CA", () => _view.LowText(low with { To = null }, "Costco", Night, Zone)));
    }

    [Fact]
    public void Changes_have_their_sign_and_tone()
    {
        var up = new AreaChange(Night.AddDays(-1), 154.9m, 162.9m, 160);
        var down = up with { NowMedianCents = 151.9m };

        // A no-break space before ¢, so the figure never wraps.
        Assert.Equal(("+8.0 ¢", "is-up"), (AreaTrendsView.Change(up), AreaTrendsView.ChangeTone(up)));
        Assert.Equal(("−3.0 ¢", "is-down"), (AreaTrendsView.Change(down), AreaTrendsView.ChangeTone(down)));
        Assert.Equal(("0.0 ¢", null), (AreaTrendsView.Change(up with { NowMedianCents = 154.9m }), AreaTrendsView.ChangeTone(up with { NowMedianCents = 154.9m })));
        Assert.Equal(("—", null), (AreaTrendsView.Change(null), AreaTrendsView.ChangeTone(null)));
    }

    [Fact]
    public void The_card_needs_an_area_and_prices()
    {
        Assert.True(AreaTrendsView.Shows(Trends()));
        Assert.False(AreaTrendsView.Shows(Trends() with { Stations = 2 }));
        Assert.False(AreaTrendsView.Shows(Trends() with { RadiusKm = null }));
        Assert.False(AreaTrendsView.Shows(Trends() with { Now = null }));
        Assert.False(AreaTrendsView.Shows(null));
    }

    private static void Culture(string name) =>
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    private static string Culture(string name, Func<string> text)
    {
        Culture(name);
        return Plain(text())!;
    }

    /// <summary>Plain spaces: before "a.m.", ICU writes a narrow no-break space or a plain one, depending on its version.</summary>
    private static string? Plain(string? text) =>
        text is null ? null : string.Concat(text.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c));
}
