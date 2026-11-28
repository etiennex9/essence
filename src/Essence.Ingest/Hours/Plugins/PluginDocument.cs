using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>What a plugin's answer is: told from its first character when the spec says <see cref="Auto"/>.</summary>
public enum PluginFormat
{
    Auto,
    Json,
    Csv,
    Html,
}

/// <summary>
/// A plugin's answer as JSON, whatever it came as: JSON as it is; CSV as an array of rows, each an object of its columns
/// by their header (the delimiter, ",", ";" or a tab, told from the header); HTML as an array of the JSON blocks of its
/// page (schema.org's <c>application/ld+json</c>, and <c>application/json</c> data), each <c>@graph</c> spread. Text is
/// read as UTF-8, or Windows-1252 when it isn't.
/// </summary>
public static partial class PluginDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Comments and trailing commas are let be; a key twice in an object isn't JSON that can be read for sure.</summary>
    public static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false };

    static PluginDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="what">Who answered, for the errors: "Crevier's answer".</param>
    /// <exception cref="HoursFormatException">The answer isn't of the format, or holds nothing to read.</exception>
    public static JsonNode Read(byte[] body, PluginFormat format, string what)
    {
        var text = Decode(body);
        return (format == PluginFormat.Auto ? Sniff(text) : format) switch
        {
            PluginFormat.Json => ReadJson(text, what),
            PluginFormat.Html => ReadHtml(text, what),
            _ => ReadCsv(text, what),
        };
    }

    /// <summary>JSON when it starts with <c>{</c> or <c>[</c>, HTML with <c>&lt;</c>, else CSV.</summary>
    public static PluginFormat Sniff(string text) => text.TrimStart() switch
    {
        ['{' or '[', ..] => PluginFormat.Json,
        ['<', ..] => PluginFormat.Html,
        _ => PluginFormat.Csv,
    };

    public static string Decode(byte[] body)
    {
        var start = body is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try
        {
            return StrictUtf8.GetString(body, start, body.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(body);
        }
    }

    private static JsonNode ReadJson(string text, string what)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: Lenient) ?? throw new HoursFormatException($"{what} is empty JSON.");
        }
        catch (JsonException ex)
        {
            throw new HoursFormatException($"{what} isn't JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The JSON blocks of the page, each <c>@graph</c> and array spread; a block that isn't JSON is skipped.</summary>
    private static JsonArray ReadHtml(string text, string what)
    {
        var nodes = new JsonArray();
        foreach (var json in ScriptBlocks(text))
        {
            JsonNode? block;
            try
            {
                block = JsonNode.Parse(json.Trim(), documentOptions: Lenient);
            }
            catch (JsonException)
            {
                continue;
            }

            Spread(block, nodes);
        }

        return nodes.Count > 0 ? nodes : throw new HoursFormatException($"{what} is a page without schema.org or JSON data in it.");

        static void Spread(JsonNode? node, JsonArray into)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array.ToList())
                    {
                        Spread(item?.DeepClone(), into);
                    }

                    break;
                case JsonObject obj when obj["@graph"] is JsonArray graph:
                    Spread(graph, into);
                    break;
                case JsonObject obj:
                    into.Add(obj.DeepClone());
                    break;
            }
        }
    }

    /// <summary>The rows under the header, each an object of its cells by their column (trimmed; an empty cell is left out).</summary>
    private static JsonArray ReadCsv(string text, string what)
    {
        var delimiter = Delimiter(text);
        var rows = Rows(text, delimiter).Where(r => r.Any(c => c.Length > 0)).ToList();
        if (rows.Count < 2)
        {
            throw new HoursFormatException($"{what} isn't JSON, a page or a CSV file with rows under its header.");
        }

        var header = new List<string>();
        foreach (var (cell, index) in rows[0].Select((c, i) => (c.Trim(), i)))
        {
            var name = cell.Length > 0 ? cell : $"column{index + 1}";
            var unique = name;
            for (var n = 2; header.Contains(unique, StringComparer.Ordinal); n++)
            {
                unique = $"{name}_{n}";
            }

            header.Add(unique);
        }

        var records = new JsonArray();
        foreach (var row in rows.Skip(1))
        {
            var record = new JsonObject();
            for (var i = 0; i < Math.Min(row.Count, header.Count); i++)
            {
                if (row[i].Trim() is { Length: > 0 } cell)
                {
                    record[header[i]] = cell;
                }
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>The header's delimiter: the most common of ",", ";" and a tab outside quotes.</summary>
    private static char Delimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\n' or '\r')
            {
                break;
            }
            else if (!quoted && counts.ContainsKey(c))
            {
                counts[c]++;
            }
        }

        return counts.MaxBy(c => c.Value) is { Value: > 0 } best ? best.Key : ',';
    }

    /// <summary>RFC 4180's rows: a quoted cell may hold the delimiter, line breaks and "" for a quote.</summary>
    private static IEnumerable<List<string>> Rows(string text, char delimiter)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                yield return row;
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }

    /// <summary>
    /// The text of a page's markup, for hours written in HTML: line breaks kept, tags dropped, entities decoded. Tags are
    /// found moving forward only: a "&lt;" never closed leaves the rest as text, in one pass.
    /// </summary>
    public static string TextOf(string html)
    {
        var broken = BreakPattern().Replace(html, "\n");
        var text = new StringBuilder(broken.Length);
        for (var at = 0; at < broken.Length;)
        {
            var open = broken.IndexOf('<', at);
            var close = open < 0 ? -1 : broken.IndexOf('>', open + 1);
            if (close < 0)
            {
                text.Append(broken, at, broken.Length - at);
                break;
            }

            text.Append(broken, at, open - at).Append(' ');
            at = close + 1;
        }

        return WebUtility.HtmlDecode(text.ToString());
    }

    /// <summary>
    /// The text of each <c>&lt;script type="application/ld+json"&gt;</c> (or <c>application/json</c>) of the page, found by
    /// moving forward only, so a page of any size, or one whose tags never close, takes one pass.
    /// </summary>
    public static IEnumerable<string> ScriptBlocks(string html)
    {
        for (var at = 0; at < html.Length;)
        {
            var open = html.IndexOf("<script", at, StringComparison.OrdinalIgnoreCase);
            var tagEnd = open < 0 ? -1 : html.IndexOf('>', open);
            if (tagEnd < 0)
            {
                yield break;
            }

            var close = html.IndexOf("</script", tagEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                yield break;
            }

            var attributes = html[(open + "<script".Length)..tagEnd];
            if (attributes is [] or [' ' or '\t' or '\r' or '\n' or '/', ..] && JsonTypePattern().IsMatch(attributes))
            {
                yield return html[(tagEnd + 1)..close];
            }

            at = close + "</script".Length;
        }
    }

    [GeneratedRegex("""\btype\s*=\s*["']?application/(?:ld\+)?json\b""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonTypePattern();

    [GeneratedRegex(@"<br\s*/?>|</p>|</li>|</tr>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

}
