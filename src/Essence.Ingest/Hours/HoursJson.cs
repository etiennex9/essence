using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Essence.Ingest.Hours;

/// <summary>Reading the sources' JSON: what a field holds, or null when it holds something else.</summary>
internal static class HoursJson
{
    /// <exception cref="HoursFormatException">The body isn't JSON: a block page, an error page.</exception>
    public static JsonNode? Parse(byte[] body, string source)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HoursFormatException($"{source} didn't answer with JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The node's UTF-8 JSON, as kept on disk: written without reflection, so trimming keeps it working.</summary>
    public static byte[] Bytes(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            node.WriteTo(writer);
        }

        return stream.ToArray();
    }

    /// <summary>A string, trimmed; null when it's missing, empty or not a string.</summary>
    public static string? Text(JsonObject node, string field) =>
        node[field] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>A number, or a string that holds one (Ultramar writes its coordinates so).</summary>
    public static double? Number(JsonObject node, string field) => node[field] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.GetValue<double>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.String
            && double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    public static bool IsTrue(JsonObject node, string field) => node[field]?.GetValueKind() == JsonValueKind.True;

    /// <summary>A copy of <paramref name="node"/> with only <paramref name="fields"/>, those it has: what's kept on disk.</summary>
    public static JsonObject Keep(JsonObject node, IEnumerable<string> fields)
    {
        var kept = new JsonObject();
        foreach (var field in fields)
        {
            if (node.TryGetPropertyValue(field, out var value))
            {
                kept[field] = value?.DeepClone();
            }
        }

        return kept;
    }

    /// <summary>A point that's one: latitude and longitude in range, not 0,0.</summary>
    public static bool IsPoint(double? latitude, double? longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && !(latitude == 0 && longitude == 0);
}
