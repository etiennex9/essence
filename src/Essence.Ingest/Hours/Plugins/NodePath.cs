using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>
/// Where a field is in a record, as a plugin's spec writes it (its <c>fields</c> hints) and as the mapping found says it:
/// <c>location.coordinates.latitude</c>; <c>services[]</c> or <c>services.*</c> for each item of an array or value of an
/// object; <c>services[serviceType=Forecourt]</c> for the items whose <c>serviceType</c> is <c>Forecourt</c> (in any
/// case); <c>days[0]</c> for an item; and <c>a | b</c> for the first of them that gives anything.
/// </summary>
public sealed class NodePath
{
    private readonly IReadOnlyList<IReadOnlyList<Step>> _alternatives;

    private NodePath(string text, IReadOnlyList<IReadOnlyList<Step>> alternatives) => (Text, _alternatives) = (text, alternatives);

    public string Text { get; }

    /// <summary>A path of one property: <c>locationId</c>.</summary>
    public static NodePath Of(params string[] keys) => new(string.Join('.', keys), [[.. keys.Select(k => (Step)new Property(k))]]);

    public static NodePath Parse(string text) =>
        TryParse(text, out var path, out var error) ? path! : throw new FormatException(error);

    public static bool TryParse(string? text, out NodePath? path, out string? error)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the path is empty";
            return false;
        }

        var alternatives = new List<IReadOnlyList<Step>>();
        foreach (var alternative in text.Split('|', StringSplitOptions.TrimEntries))
        {
            if (ParseSteps(alternative, out error) is not { } steps)
            {
                error = $"\"{alternative}\": {error}";
                return false;
            }

            alternatives.Add(steps);
        }

        path = new NodePath(string.Join(" | ", text.Split('|', StringSplitOptions.TrimEntries)), alternatives);
        error = null;
        return true;
    }

    /// <summary>What the first alternative that gives anything gives: nulls left out.</summary>
    public IReadOnlyList<JsonNode> Select(JsonNode? node)
    {
        foreach (var steps in _alternatives)
        {
            var found = Walk(node, steps, 0).OfType<JsonNode>().ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    public JsonNode? First(JsonNode? node) => Select(node).FirstOrDefault();

    public override string ToString() => Text;

    private static IEnumerable<JsonNode?> Walk(JsonNode? node, IReadOnlyList<Step> steps, int index)
    {
        if (index == steps.Count)
        {
            yield return node;
            yield break;
        }

        switch (steps[index])
        {
            case Property(var name):
                if (node is JsonObject obj && obj.TryGetPropertyValue(name, out var child))
                {
                    foreach (var found in Walk(child, steps, index + 1))
                    {
                        yield return found;
                    }
                }

                break;

            case Item(var position):
                if (node is JsonArray array && position < array.Count)
                {
                    foreach (var found in Walk(array[position], steps, index + 1))
                    {
                        yield return found;
                    }
                }

                break;

            case Each(var key, var value):
                IEnumerable<JsonNode?> children = node switch
                {
                    JsonArray items => items,
                    JsonObject members => members.Select(m => m.Value),
                    _ => [],
                };
                foreach (var item in children)
                {
                    if (key is not null && !(item is JsonObject candidate && Equals(candidate[key], value)))
                    {
                        continue;
                    }

                    foreach (var found in Walk(item, steps, index + 1))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    /// <summary>A filter's value: a string in any case, or the text of a number or a boolean.</summary>
    private static bool Equals(JsonNode? node, string? value) =>
        node is JsonValue scalar && value is not null && scalar.GetValueKind() switch
        {
            JsonValueKind.String => string.Equals(scalar.GetValue<string>().Trim(), value, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => string.Equals(scalar.ToJsonString(), value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static List<Step>? ParseSteps(string text, out string? error)
    {
        var steps = new List<Step>();
        var name = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.')
            {
                if (!Flush())
                {
                    if (i == 0 || text[i - 1] != ']')
                    {
                        error = "a name is missing before a '.'";
                        return null;
                    }
                }

                continue;
            }

            if (c == '[')
            {
                Flush();
                var close = text.IndexOf(']', i);
                if (close < 0)
                {
                    error = "a '[' isn't closed";
                    return null;
                }

                var inside = text[(i + 1)..close].Trim();
                if (inside is "" or "*")
                {
                    steps.Add(new Each(null, null));
                }
                else if (int.TryParse(inside, out var position) && position >= 0)
                {
                    steps.Add(new Item(position));
                }
                else if (inside.Split('=', 2, StringSplitOptions.TrimEntries) is [{ Length: > 0 } key, var value])
                {
                    steps.Add(new Each(key, value));
                }
                else
                {
                    error = $"\"[{inside}]\" isn't [], [*], [n] or [key=value]";
                    return null;
                }

                i = close;
                continue;
            }

            name.Append(c);
        }

        Flush();
        if (steps.Count == 0)
        {
            error = "the path is empty";
            return null;
        }

        error = null;
        return steps;

        bool Flush()
        {
            var part = name.ToString().Trim();
            name.Clear();
            if (part.Length == 0)
            {
                return false;
            }

            steps.Add(part == "*" ? new Each(null, null) : new Property(part));
            return true;
        }
    }

    private abstract record Step;

    private sealed record Property(string Name) : Step;

    private sealed record Item(int Position) : Step;

    /// <param name="Key">With <paramref name="Value"/>: only the items whose field is that value.</param>
    private sealed record Each(string? Key, string? Value) : Step;
}
