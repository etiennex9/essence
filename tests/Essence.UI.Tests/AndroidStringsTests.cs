using System.Xml.Linq;

namespace Essence.UI.Tests;

/// <summary>
/// The few texts Android itself shows of the app (the widget picker, the widget settings' title) are in the Android project's
/// resources rather than in Strings.resx; like Strings.resx, every one is in English and French.
/// </summary>
public class AndroidStringsTests
{
    [Fact]
    public void Every_android_text_is_in_english_and_french()
    {
        var resources = Path.Combine(RepositoryRoot(), "src", "Essence.Mobile", "Platforms", "Android", "Resources");

        var english = Names(Path.Combine(resources, "values", "strings.xml"));
        var french = Names(Path.Combine(resources, "values-fr", "strings.xml"));

        Assert.NotEmpty(english);
        Assert.Equal(english, french);
    }

    private static SortedSet<string> Names(string path) =>
        new(XDocument.Load(path).Root!.Elements("string").Select(e => (string)e.Attribute("name")!), StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Essence.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Essence.slnx not found above the test's folder.");
    }
}
