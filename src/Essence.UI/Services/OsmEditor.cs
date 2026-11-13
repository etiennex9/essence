using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Essence.Hours;

namespace Essence.UI.Services;

/// <summary>An OpenStreetMap element: <c>node/123</c>, <c>way/456</c> or <c>relation/789</c>.</summary>
public readonly record struct OsmElementRef(string Type, long Id)
{
    /// <summary>A node or a way: the app edits those. A relation's hours are left to OpenStreetMap's own editors.</summary>
    public bool IsEditable => Type is "node" or "way";

    /// <summary>Where a person sees it: <c>https://www.openstreetmap.org/node/123</c>, on <paramref name="site"/>.</summary>
    public Uri PageOn(Uri site) => new(site, ToString());

    public static bool TryParse(string? text, out OsmElementRef element)
    {
        element = default;
        if (text?.Split('/') is not [var type and ("node" or "way" or "relation"), var id]
            || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return false;
        }

        element = new OsmElementRef(type, number);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Type}/{Id}");
}

/// <summary>A correction of an element's hours, as the user saved it.</summary>
/// <param name="Rule">The new <c>opening_hours</c>.</param>
/// <param name="CheckDate">Today in the station's zone, for <c>check_date:opening_hours</c>.</param>
/// <param name="CreatedBy">The changeset's <c>created_by</c>: <c>Essence 2026.11.13</c>.</param>
/// <param name="Comment">The changeset's comment: "Update opening hours of Costco".</param>
/// <param name="Surveyed">The user checked the hours at the station: the changeset's <c>source</c> is <c>survey</c>, and only then.</param>
public sealed record OsmHoursEdit(OsmElementRef Element, string Rule, DateOnly CheckDate, string CreatedBy, string Comment, bool Surveyed);

/// <summary>What stopped an edit, for the editor to say it in the user's language.</summary>
public enum OsmEditFailure
{
    /// <summary>OpenStreetMap refused it, and its reason, in its own words, is the exception's message.</summary>
    Refused,

    /// <summary>OpenStreetMap can't be reached.</summary>
    Unreachable,

    /// <summary>The element was deleted from OpenStreetMap since the app last read it.</summary>
    Deleted,

    /// <summary>
    /// The element's hours on OpenStreetMap say more than the app can (dates, a comment, school holidays, a fallback…):
    /// replacing them would lose that, so they're left to OpenStreetMap's own editors.
    /// </summary>
    DetailedHours,

    /// <summary>A relation, left to OpenStreetMap's own editors.</summary>
    NotEditable,

    /// <summary>No account is linked, or OpenStreetMap no longer takes its token (401): it's to be linked again.</summary>
    NotLinked,

    /// <summary>OpenStreetMap answered something else than it should.</summary>
    BadAnswer,
}

/// <summary>An edit that didn't go through: why (<see cref="Failure"/>), and a message in English for the logs.</summary>
/// <param name="status">The answer's status; null when it was another problem.</param>
public sealed class OsmEditException(OsmEditFailure failure, string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public OsmEditFailure Failure { get; } = failure;

    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Sends a correction of a station's hours to OpenStreetMap's API 0.6, as the user, with their own token (<see cref="OsmAccount"/>):
/// the element as it is now, a changeset of its own, the element with only <c>opening_hours</c> and
/// <c>check_date:opening_hours</c> changed (every other tag exactly as it was), then the changeset closed. Hours there that
/// the app can't read (they say more than it can) are never replaced: nothing is opened for them. A version conflict (409:
/// someone edited it meanwhile) fetches it again and tries once more. The changeset is closed whatever happens once it's
/// open, so a failure never leaves one open. Never creates or deletes anything. XML is written with <see cref="XmlWriter"/>,
/// which escapes every value. Pure: the <see cref="HttpClient"/> is given, and every address is absolute.
/// </summary>
public sealed class OsmEditor(HttpClient http)
{
    public const string HoursTag = "opening_hours";
    public const string CheckDateTag = "check_date:opening_hours";

    /// <summary>The longest of OpenStreetMap's error text shown: past it, the reason is cut short.</summary>
    private const int MaxReasonLength = 200;

    /// <summary>OpenStreetMap's longest tag value, in Unicode characters (not UTF-16 units, as a string's length counts).</summary>
    private const int MaxValueLength = 255;

    /// <summary>Sends the edit; returns the changeset it's in.</summary>
    /// <param name="apiUrl">The API's root: <c>https://api.openstreetmap.org</c>.</param>
    /// <exception cref="OsmEditException">OpenStreetMap refused it, or can't be reached; nothing more was changed.</exception>
    public async Task<long> UpdateHoursAsync(Uri apiUrl, string accessToken, OsmHoursEdit edit, CancellationToken cancellationToken)
    {
        if (!edit.Element.IsEditable)
        {
            throw new OsmEditException(OsmEditFailure.NotEditable, $"{edit.Element.Type}s aren't edited here.");
        }

        var root = new Uri(apiUrl.AbsoluteUri.TrimEnd('/') + "/api/0.6/");
        // Built before a changeset is opened, so what would stop the edit (hours it can't replace) stops it first.
        var upload = WithHours(await FetchAsync(root, accessToken, edit.Element, cancellationToken), edit);
        var changeset = await CreateChangesetAsync(root, accessToken, edit, cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var response = await SendAsync(
                    HttpMethod.Put, new Uri(root, edit.Element.ToString()), accessToken, Xml(InChangeset(upload, changeset)), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return changeset;
                }

                if (response.StatusCode == HttpStatusCode.Conflict && attempt == 0)
                {
                    // Edited by someone else since it was fetched: its newer version, with their changes, gets ours (unless
                    // they wrote hours it can't replace).
                    upload = WithHours(await FetchAsync(root, accessToken, edit.Element, cancellationToken), edit);
                    continue;
                }

                throw await RefusedAsync(response, cancellationToken);
            }
        }
        finally
        {
            await CloseAsync(root, accessToken, changeset);
        }
    }

    /// <summary>
    /// The changeset's XML: <c>created_by</c>, the comment, and <c>source=survey</c> only when the user checked the hours
    /// at the station.
    /// </summary>
    public static XDocument ChangesetXml(OsmHoursEdit edit)
    {
        var changeset = new XElement("changeset", Tag("created_by", Capped(edit.CreatedBy)), Tag("comment", Capped(edit.Comment)));
        if (edit.Surveyed)
        {
            changeset.Add(Tag("source", "survey"));
        }

        return new XDocument(new XElement("osm", changeset));
    }

    /// <summary>
    /// The element to upload, but for its changeset (<see cref="InChangeset"/>): its id, its version as fetched, a node's
    /// point or a way's nodes in their order, and its tags in their order, exactly as they were, with <c>opening_hours</c>
    /// and <c>check_date:opening_hours</c> set (added at the end when it had none). Who edited it last and when are left
    /// out: the API sets them.
    /// </summary>
    /// <param name="fetched">The element as the API gave it: <c>&lt;osm&gt;&lt;node …&gt;…&lt;/node&gt;&lt;/osm&gt;</c>.</param>
    /// <exception cref="OsmEditException">
    /// Its <c>opening_hours</c> are ones the app can't read (<see cref="OsmEditFailure.DetailedHours"/>): they say more than it
    /// could put back. Or the answer isn't the element.
    /// </exception>
    public static XElement WithHours(XDocument fetched, OsmHoursEdit edit)
    {
        var source = fetched.Root?.Element(edit.Element.Type) is { } found && (string?)found.Attribute("id") == IdOf(edit.Element)
            ? found
            : throw BadAnswer($"OpenStreetMap answered no {edit.Element}.");
        if (source.Elements("tag").FirstOrDefault(t => (string?)t.Attribute("k") == HoursTag) is { } hours
            && (string?)hours.Attribute("v") is { } current && !string.IsNullOrWhiteSpace(current)
            && !OpeningHours.TryParse(current, lenient: true, out _, out _))
        {
            throw new OsmEditException(OsmEditFailure.DetailedHours, $"{edit.Element}'s hours can't be read: \"{current}\".");
        }

        var element = new XElement(
            edit.Element.Type,
            new XAttribute("id", IdOf(edit.Element)),
            new XAttribute("version", (string?)source.Attribute("version") ?? throw BadAnswer($"{edit.Element} has no version.")));
        if (edit.Element.Type == "node")
        {
            element.Add(
                new XAttribute("lat", (string?)source.Attribute("lat") ?? throw BadAnswer($"{edit.Element} has no point.")),
                new XAttribute("lon", (string?)source.Attribute("lon") ?? throw BadAnswer($"{edit.Element} has no point.")));
        }
        else
        {
            var nodes = source.Elements("nd").Select(nd => (string?)nd.Attribute("ref")).ToList();
            if (nodes.Count == 0 || nodes.Any(n => n is null))
            {
                throw BadAnswer($"{edit.Element} has no nodes.");
            }

            element.Add(nodes.Select(n => new XElement("nd", new XAttribute("ref", n!))));
        }

        var changes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HoursTag] = edit.Rule,
            [CheckDateTag] = edit.CheckDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        foreach (var tag in source.Elements("tag"))
        {
            var key = (string?)tag.Attribute("k");
            if (key is null)
            {
                continue;
            }

            element.Add(Tag(key, changes.Remove(key, out var changed) ? changed : (string?)tag.Attribute("v") ?? ""));
        }

        foreach (var (key, value) in changes)
        {
            element.Add(Tag(key, value));
        }

        return element;
    }

    /// <summary>The element's upload in its changeset, said after its version: <c>&lt;osm&gt;&lt;node id version changeset …&gt;</c>.</summary>
    public static XDocument InChangeset(XElement element, long changeset)
    {
        var attributes = element.Attributes().ToList();
        attributes.Insert(2, new XAttribute("changeset", changeset.ToString(CultureInfo.InvariantCulture)));
        return new XDocument(new XElement("osm", new XElement(element.Name, attributes, element.Elements())));
    }

    private async Task<XDocument> FetchAsync(Uri root, string accessToken, OsmElementRef element, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(root, element.ToString()), accessToken, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode == HttpStatusCode.Gone
                ? new OsmEditException(OsmEditFailure.Deleted, $"{element} was deleted from OpenStreetMap.", HttpStatusCode.Gone)
                : await RefusedAsync(response, cancellationToken);
        }

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await XDocument.LoadAsync(body, LoadOptions.None, cancellationToken);
        }
        catch (XmlException ex)
        {
            throw new OsmEditException(OsmEditFailure.BadAnswer, $"OpenStreetMap answered something else than {element}.", inner: ex);
        }
    }

    private async Task<long> CreateChangesetAsync(Uri root, string accessToken, OsmHoursEdit edit, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, new Uri(root, "changeset/create"), accessToken, Xml(ChangesetXml(edit)), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await RefusedAsync(response, cancellationToken);
        }

        var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw BadAnswer("OpenStreetMap answered no changeset.");
    }

    /// <summary>
    /// Closes the changeset, even when the edit failed or was cancelled. A failure to close is left be: OpenStreetMap closes
    /// a changeset by itself an hour after its last edit.
    /// </summary>
    private async Task CloseAsync(Uri root, string accessToken, long changeset)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Put, new Uri(root, string.Create(CultureInfo.InvariantCulture, $"changeset/{changeset}/close")), accessToken, null,
                CancellationToken.None);
        }
        catch (OsmEditException)
        {
            // Unreachable: closed by OpenStreetMap within the hour. What failed before, if anything, is what's said.
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri url, string accessToken, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new OsmEditException(OsmEditFailure.Unreachable, "OpenStreetMap can't be reached.", inner: ex);
        }
    }

    /// <summary>
    /// OpenStreetMap's reason, as its API says it in plain text, or its status when it says none. A token it no longer
    /// takes (401) is <see cref="OsmEditFailure.NotLinked"/>.
    /// </summary>
    private static async Task<OsmEditException> RefusedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = response.Content.Headers.ContentType?.MediaType is "text/plain"
            ? Cut((await response.Content.ReadAsStringAsync(cancellationToken)).Trim(), MaxReasonLength)
            : "";
        if (text.Length == 0)
        {
            text = $"{(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}";
        }

        var failure = response.StatusCode == HttpStatusCode.Unauthorized ? OsmEditFailure.NotLinked : OsmEditFailure.Refused;
        return new OsmEditException(failure, text, response.StatusCode);
    }

    private static OsmEditException BadAnswer(string message) => new(OsmEditFailure.BadAnswer, message);

    private static string IdOf(OsmElementRef element) => element.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A tag, its value as given: an element's own go back exactly as they came.</summary>
    private static XElement Tag(string key, string value) => new("tag", new XAttribute("k", key), new XAttribute("v", value));

    /// <summary>One of the app's own values (a changeset's), within OpenStreetMap's 255 characters: a long name's comment is cut short.</summary>
    private static string Capped(string value) => Cut(value, MaxValueLength);

    /// <summary>
    /// The text, or as much of it as fits in <paramref name="max"/> Unicode characters with "…" at its end. Counted, and
    /// cut, in characters (runes), never through one: an emoji is two UTF-16 units, which XML refuses split.
    /// </summary>
    public static string Cut(string text, int max)
    {
        // A string's length counts UTF-16 units, never fewer than its characters: within it, it fits.
        if (text.Length <= max)
        {
            return text;
        }

        var (characters, end) = (0, 0);
        foreach (var rune in text.EnumerateRunes())
        {
            if (++characters == max)
            {
                // The last character that fits: the text is whole if nothing comes after it, else "…" takes its place.
                return end + rune.Utf16SequenceLength == text.Length ? text : text[..end] + "…";
            }

            end += rune.Utf16SequenceLength;
        }

        return text;
    }

    /// <summary>The document as a request's body (the request disposes of it).</summary>
    private static ByteArrayContent Xml(XDocument document)
    {
        var content = new ByteArrayContent(Bytes(document));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>The XML as it's sent, for the tests.</summary>
    public static string ToText(XDocument document) => Encoding.UTF8.GetString(Bytes(document));

    /// <summary>The document as OpenStreetMap takes it: UTF-8 XML, every value escaped by the writer.</summary>
    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            document.Save(writer);
        }

        return stream.ToArray();
    }
}
