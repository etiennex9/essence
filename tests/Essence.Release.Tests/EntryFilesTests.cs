namespace Essence.Release.Tests;

public class EntryFilesTests
{
    [Fact]
    public void An_entry_has_english_and_french_and_maybe_a_date()
    {
        var entry = EntryFiles.Parse("changelog/map.json", """{"en": " A map. ", "fr": "Une carte.", "date": "2026-10-13"}""");

        Assert.Equal("A map.", entry.Text.En);
        Assert.Equal("Une carte.", entry.Text.Fr);
        Assert.Equal(new DateOnly(2026, 10, 13), entry.Date);
        Assert.Null(EntryFiles.Parse("changelog/map.json", """{"en": "A map.", "fr": "Une carte."}""").Date);
    }

    [Theory]
    [InlineData("""{"en": "A map."}""", "needs both")]
    [InlineData("""{"en": "A map.", "fr": "  "}""", "non-empty")]
    [InlineData("""{"en": "A map.", "fr": "Une carte.", "fr-CA": "Une carte."}""", "unknown key")]
    [InlineData("""{"en": "A map.", "fr": "Une carte.", "date": "13/10/2026"}""", "date")]
    [InlineData("""["A map."]""", "object")]
    [InlineData("""{"en": "A map.",}""", "not valid JSON")]
    public void A_bad_entry_says_what_is_wrong(string json, string problem)
    {
        var ex = Assert.Throws<ReleaseException>(() => EntryFiles.Parse("changelog/map.json", json));

        Assert.StartsWith("changelog/map.json: ", ex.Message);
        Assert.Contains(problem, ex.Message);
    }

    [Fact]
    public void Every_entry_in_the_repository_is_valid()
    {
        var root = RepositoryRoot();
        Assert.True(Directory.Exists(Path.Combine(root, EntryFiles.Folder)), "changelog/ is missing");

        // ReadAll parses each file, so a bad one fails here with its name and the reason.
        var entries = EntryFiles.ReadAll(root);

        Assert.NotEmpty(entries);
        var today = Days.Today(TimeProvider.System);
        Assert.All(entries, e => Assert.True(e.Date is null || e.Date <= today, $"{e.Path}: its date is in the future"));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Essence.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Essence.slnx not found above " + AppContext.BaseDirectory);
    }
}
