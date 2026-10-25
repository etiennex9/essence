using System.Globalization;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData("en-CA", "164.9")]
    [InlineData("fr-CA", "164,9")]
    public void Price_uses_the_culture_decimal_separator(string culture, string expected) =>
        Assert.Equal(expected, Formatting.Price(164.9m, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Price_always_shows_one_decimal() =>
        Assert.Equal("165.0", Formatting.Price(165m, CultureInfo.InvariantCulture));

    [Fact]
    public void Missing_price_is_a_dash() =>
        Assert.Equal("—", Formatting.Price(null, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("en-CA", "$64.75")]
    [InlineData("en-US", "$64.75")]
    [InlineData("en-GB", "$64.75")]
    [InlineData("fr-CA", "64,75 $")]
    [InlineData("fr-FR", "64,75 $")]
    public void Money_is_in_dollars_whatever_the_culture(string culture, string expected) =>
        // French puts a no-break space before the symbol, narrow or not depending on the ICU version.
        Assert.Equal(expected, string.Concat(Formatting.Money(64.75m, CultureInfo.GetCultureInfo(culture))
            .Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c)));

    [Fact]
    public void Money_always_shows_cents() =>
        Assert.Equal("$2.20", Formatting.Money(2.2m, CultureInfo.GetCultureInfo("en-CA")));

    [Theory]
    [InlineData("en-CA", "9.0")]
    [InlineData("fr-CA", "9,0")]
    public void Km_has_one_decimal(string culture, string expected) =>
        Assert.Equal(expected, Formatting.Km(9.04, CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData(30, AgoUnit.JustNow, 0)]
    [InlineData(90, AgoUnit.Minutes, 1)]
    [InlineData(59 * 60, AgoUnit.Minutes, 59)]
    [InlineData(3 * 3600 + 120, AgoUnit.Hours, 3)]
    [InlineData(47 * 3600, AgoUnit.Hours, 47)]
    [InlineData(49 * 3600, AgoUnit.Days, 2)]
    public void Ago_picks_the_unit(int seconds, AgoUnit unit, int value) =>
        Assert.Equal((unit, value), Formatting.Ago(TimeSpan.FromSeconds(seconds)));
}
