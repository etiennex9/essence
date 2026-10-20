using System.Globalization;
using System.Text.Json;
using Essence.Contracts;

namespace Essence.Release;

/// <summary>
/// Reads <c>changelog/*.json</c>: <c>{"en": "…", "fr": "…"}</c>, plain text, plus <c>"date": "YYYY-MM-DD"</c> only for an
/// entry written after the fact. Anything else is an error, so a typo doesn't drop a translation silently.
/// </summary>
public static class EntryFiles
{
    public const string Folder = "changelog";

    /// <summary>The entries in <paramref name="root"/>'s changelog folder, sorted by file name.</summary>
    public static List<Entry> ReadAll(string root)
    {
        var folder = Path.Combine(root, Folder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*.json")
            .Select(file => Parse($"{Folder}/{Path.GetFileName(file)}", File.ReadAllText(file)))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <exception cref="ReleaseException">The file isn't a valid entry; the message says why.</exception>
    public static Entry Parse(string path, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ReleaseException($"{path}: not valid JSON ({ex.Message})");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseException($"{path}: expected an object with \"en\" and \"fr\"");
            }

            string? en = null, fr = null;
            DateOnly? date = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "en":
                        en = Text(path, property);
                        break;
                    case "fr":
                        fr = Text(path, property);
                        break;
                    case "date":
                        date = property.Value.ValueKind == JsonValueKind.String
                               && DateOnly.TryParseExact(property.Value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                            ? d
                            : throw new ReleaseException($"{path}: \"date\" must be a date like \"2026-10-19\"");
                        break;
                    default:
                        throw new ReleaseException($"{path}: unknown key \"{property.Name}\" (only \"en\", \"fr\" and \"date\")");
                }
            }

            if (en is null || fr is null)
            {
                throw new ReleaseException($"{path}: needs both \"en\" and \"fr\"");
            }

            return new Entry(path, new ChangelogEntry(en, fr), date);
        }
    }

    private static string Text(string path, JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : throw new ReleaseException($"{path}: \"{property.Name}\" must be a non-empty string");
}
