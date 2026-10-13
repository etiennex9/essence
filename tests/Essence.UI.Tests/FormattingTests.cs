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
    [InlineData(30, AgoUnit.JustNow, 0)]
    [InlineData(90, AgoUnit.Minutes, 1)]
    [InlineData(59 * 60, AgoUnit.Minutes, 59)]
    [InlineData(3 * 3600 + 120, AgoUnit.Hours, 3)]
    [InlineData(47 * 3600, AgoUnit.Hours, 47)]
    [InlineData(49 * 3600, AgoUnit.Days, 2)]
    public void Ago_picks_the_unit(int seconds, AgoUnit unit, int value) =>
        Assert.Equal((unit, value), Formatting.Ago(TimeSpan.FromSeconds(seconds)));
}
