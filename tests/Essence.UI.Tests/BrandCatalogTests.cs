using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class BrandCatalogTests
{
    [Theory]
    [InlineData("Esso", "esso")]
    [InlineData("Petro-Canada", "petro-canada")]
    [InlineData("Pétro-T", "petro-t")]
    [InlineData("Nutrinor Énergies", "nutrinor-energies")]
    [InlineData("  Canadian   Tire ", "canadian-tire")]
    [InlineData("R.L.", "r-l")]
    [InlineData("Les Huiles Berthier Inc", "les-huiles-berthier-inc")]
    [InlineData("Énergie Express BD", "energie-express-bd")]
    public void Slugify_folds_accents_and_separators(string brand, string expected) =>
        Assert.Equal(expected, Brands.Slugify(brand));

    [Theory]
    [InlineData("Costco", true)]
    [InlineData(" COSTCO ", true)]
    [InlineData("Costco Wholesale", false)]
    [InlineData("Esso", false)]
    [InlineData(null, false)]
    public void Costco_sells_to_members_only(string? brand, bool expected) =>
        Assert.Equal(expected, Brands.IsMembersOnly(brand));

    [Theory]
    [InlineData("Esso", "E")]
    [InlineData("Petro-Canada", "PC")]
    [InlineData("Couche-Tard", "CT")]
    [InlineData("R.L.", "RL")]
    [InlineData("Nutrinor Énergies", "NÉ")]
    [InlineData("Les Huiles Berthier Inc", "LH")]
    public void Monogram_takes_the_first_letter_of_two_words(string brand, string expected) =>
        Assert.Equal(expected, BrandCatalog.Monogram(brand));

    [Fact]
    public void Known_brands_get_their_colors()
    {
        var shell = BrandCatalog.For("Shell");
        Assert.Equal(("#FFD500", "#C8102E"), (shell.Background, shell.Foreground));
        Assert.Equal("S", shell.Monogram);
        Assert.Null(shell.LogoUrl);
    }

    [Fact]
    public void Other_brands_get_a_stable_color()
    {
        var first = BrandCatalog.For("Gaz-O-Bar");
        var second = BrandCatalog.For("Gaz-O-Bar");
        Assert.Equal(first.Background, second.Background);
        Assert.StartsWith("#", first.Background);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_brand_has_no_monogram(string? brand)
    {
        var style = BrandCatalog.For(brand);
        Assert.Null(style.Name);
        Assert.Equal("", style.Monogram);
    }
}
