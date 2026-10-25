using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Essence.Ranking;

/// <summary>One of Photon's results: an OpenStreetMap feature and its address, in the language asked for.</summary>
/// <param name="Type">Photon's layer: <c>house</c> (anything with a house number, or a point of interest), <c>street</c>,
/// <c>locality</c>, <c>district</c>, <c>city</c>, <c>county</c>, <c>state</c>, <c>country</c> or <c>other</c>.</param>
/// <param name="OsmKey">The feature's main OpenStreetMap tag (<c>highway</c>, <c>amenity</c>, <c>place</c>…).</param>
/// <param name="State">The province, in the language asked for ("Québec", "Quebec", "Ontario").</param>
public sealed record PhotonPlace(
    LatLon Location,
    string Type,
    string? OsmKey,
    string? OsmValue,
    string? Name,
    string? HouseNumber,
    string? Street,
    string? Postcode,
    string? Locality,
    string? District,
    string? City,
    string? County,
    string? State,
    string? CountryCode);

/// <summary>What <see cref="PhotonClient.SearchAsync"/> asks for.</summary>
/// <param name="Language"><c>fr</c> or <c>en</c>: the names and the address come in it.</param>
/// <param name="Near">Results near this point rank higher (Photon's location bias); null for none.</param>
/// <param name="Limit">At most this many results.</param>
public sealed record PhotonSearch(string Query, string Language, LatLon? Near, int Limit);

public enum PhotonFailure
{
    /// <summary>Photon is down, starting, or out of reach: an answer may come later.</summary>
    Unavailable,

    /// <summary>Photon refused the request as it was made, or answered with something else than its GeoJSON: a bug.</summary>
    Bug,
}

public sealed class PhotonException(PhotonFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public PhotonFailure Failure { get; } = failure;
}

/// <summary>
/// Calls to a Photon server (address search over OpenStreetMap) for the places a trip can go to. Every failure is a
/// <see cref="PhotonException"/>; only cancellation by the caller throws something else. The timeout belongs to the
/// <see cref="HttpClient"/> (the API's: 1.5 s, no retry, since a keystroke later the answer isn't wanted any more).
/// </summary>
public sealed class PhotonClient(HttpClient http)
{
    /// <summary>
    /// Features that aren't places to drive to: streams and rivers, lakes, peaks and woods, the land's use, and the
    /// boundaries of the province, regions or the country.
    /// </summary>
    private static readonly string[] LeftOut = ["!waterway", "!natural", "!landuse", "!boundary"];

    /// <summary>The layers worth driving to: a house, a street, a part of a town, a town; not a county, the province or a country.</summary>
    private static readonly string[] Layers = ["house", "street", "locality", "district", "city", "other"];

    /// <summary>Places matching what was typed, the best first, within Québec's box (<see cref="QuebecArea"/>).</summary>
    public async Task<IReadOnlyList<PhotonPlace>> SearchAsync(PhotonSearch search, CancellationToken cancellationToken)
    {
        var path = new StringBuilder("api?q=").Append(Uri.EscapeDataString(search.Query))
            .Append("&lang=").Append(Uri.EscapeDataString(search.Language))
            .Append(CultureInfo.InvariantCulture, $"&limit={search.Limit}")
            .Append(CultureInfo.InvariantCulture,
                $"&bbox={QuebecArea.West:0.###},{QuebecArea.South:0.###},{QuebecArea.East:0.###},{QuebecArea.North:0.###}");
        if (search.Near is { } near)
        {
            path.Append("&lat=").Append(Format(near.Latitude)).Append("&lon=").Append(Format(near.Longitude));
        }

        foreach (var layer in Layers)
        {
            path.Append("&layer=").Append(layer);
        }

        foreach (var tag in LeftOut)
        {
            path.Append("&osm_tag=").Append(Uri.EscapeDataString(tag));
        }

        return await GetPlacesAsync(path.ToString(), cancellationToken);
    }

    /// <summary>
    /// The places nearest a point, within <paramref name="radiusKm"/>, the nearest first: an address, a street, or a point
    /// of interest. Empty where there's nothing that close (a lake, the woods).
    /// </summary>
    public async Task<IReadOnlyList<PhotonPlace>> ReverseAsync(
        LatLon point, string language, double radiusKm, int limit, CancellationToken cancellationToken)
    {
        var path = new StringBuilder("reverse?lat=").Append(Format(point.Latitude)).Append("&lon=").Append(Format(point.Longitude))
            .Append("&lang=").Append(Uri.EscapeDataString(language))
            .Append(CultureInfo.InvariantCulture, $"&radius={radiusKm:0.###}&limit={limit}")
            .Append("&layer=house&layer=street&layer=other");
        foreach (var tag in LeftOut)
        {
            path.Append("&osm_tag=").Append(Uri.EscapeDataString(tag));
        }

        return await GetPlacesAsync(path.ToString(), cancellationToken);
    }

    /// <summary>Photon's status: it answers once its index is loaded.</summary>
    public async Task StatusAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("status", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "status") != "Ok")
        {
            throw new PhotonException(PhotonFailure.Unavailable, "Photon's status isn't Ok.");
        }
    }

    private async Task<IReadOnlyList<PhotonPlace>> GetPlacesAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(path, cancellationToken);
        return Parse(document.RootElement);
    }

    /// <summary>Photon's GeoJSON answer as places; a feature without a point is skipped.</summary>
    public static IReadOnlyList<PhotonPlace> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            throw new PhotonException(PhotonFailure.Bug, "Photon answered without features.");
        }

        var places = new List<PhotonPlace>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object
                || !feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object
                || !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array
                || coordinates.GetArrayLength() != 2
                || coordinates[0].ValueKind != JsonValueKind.Number || coordinates[1].ValueKind != JsonValueKind.Number
                || !feature.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // GeoJSON's [longitude, latitude].
            places.Add(new PhotonPlace(
                new LatLon(coordinates[1].GetDouble(), coordinates[0].GetDouble()),
                Text(p, "type") ?? "other",
                Text(p, "osm_key"),
                Text(p, "osm_value"),
                Text(p, "name"),
                Text(p, "housenumber"),
                Text(p, "street"),
                Text(p, "postcode"),
                Text(p, "locality"),
                Text(p, "district"),
                Text(p, "city"),
                Text(p, "county"),
                Text(p, "state"),
                Text(p, "countrycode")));
        }

        return places;
    }

    /// <summary>A GET of JSON; anything but a 200 with JSON becomes a <see cref="PhotonException"/>.</summary>
    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Refused, unreachable or timed out: the handler's exceptions differ, the outcome doesn't.
            throw new PhotonException(PhotonFailure.Unavailable, $"Photon didn't answer ({ex.GetType().Name}).", ex);
        }

        using (response)
        {
            // Photon answers 400 to a request it refuses, with a message that repeats what was asked: not kept.
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new PhotonException(
                    (int)response.StatusCode >= 500 ? PhotonFailure.Unavailable : PhotonFailure.Bug,
                    $"Photon answered {(int)response.StatusCode}.");
            }

            try
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new PhotonException(PhotonFailure.Bug, "Photon answered 200 without JSON.", ex);
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    private static string Format(double degrees) => degrees.ToString("0.######", CultureInfo.InvariantCulture);
}
