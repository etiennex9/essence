using System.Collections;
using System.Globalization;
using System.Resources;
using Essence.UI.Resources;

namespace Essence.UI.Tests;

public class StringsTests
{
    private static readonly ResourceManager Resources = new(typeof(Strings).FullName!, typeof(Strings).Assembly);

    [Fact]
    public void French_is_used_for_french_cultures()
    {
        Assert.Equal("Fermer", Resources.GetString("Close", CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("Close", Resources.GetString("Close", CultureInfo.GetCultureInfo("en-CA")));
    }

    [Fact]
    public void Every_english_string_has_a_french_translation()
    {
        var english = Keys(CultureInfo.InvariantCulture);
        var french = Keys(CultureInfo.GetCultureInfo("fr"));
        Assert.NotEmpty(english);
        Assert.Equal(english, french);
    }

    private static SortedSet<string> Keys(CultureInfo culture) =>
        [.. Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .Select(e => (string)e.Key)];
}
