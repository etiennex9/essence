using System.ComponentModel;
using Essence.Contracts;
using Essence.Ranking;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api.Endpoints;

public static class PlaceEndpoints
{
    internal const int MinQueryLength = 3;
    internal const int MaxQueryLength = 100;

    /// <summary>A pin's lookup asks for this many places around it, to prefer an address to a nearer street.</summary>
    private const int ReverseLimit = 5;

    private static readonly string[] Languages = ["fr", "en"];

    public static IEndpointRouteBuilder MapPlaceEndpoints(this IEndpointRouteBuilder app)
    {
        // Rate limited apart from the rest: typing sends a search every few keystrokes.
        var group = app.MapGroup("/places").WithTags("Places").RequireRateLimiting(RateLimiting.SearchPolicyName);
        group.MapGet("/search", Search)
            .WithSummary("Places in Québec matching what was typed")
            .WithDescription(
                "Addresses, streets, towns and named places in Québec, from OpenStreetMap (Photon), the best match first, at most "
                + "5; with `lat` and `lon`, those near that point rank higher. Places outside Québec are left out. `q` takes 3 to "
                + "100 characters. 503 when the address search is down.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/reverse", Reverse)
            .WithSummary("What a point in Québec is called")
            .WithDescription(
                "The nearest address within 100 m of the point, else the nearest street or named place within 1 km. 404 when "
                + "nothing is that close (a lake, the woods). 422 for a point outside Québec's boundary, which is known without "
                + "the address search; 503 when the address search is down.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<Results<Ok<List<PlaceSuggestion>>, ValidationProblem, ProblemHttpResult>> Search(
        PhotonClient photon,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        [Description("What was typed: 3 to 100 characters.")] string? q = null,
        [Description("Latitude of the user, in degrees: places near them rank higher. With `lon`.")] double? lat = null,
        [Description("Longitude of the user, in degrees. With `lat`.")] double? lon = null,
        [Description("fr (the default) or en: the language of the names and addresses.")] string? lang = null)
    {
        var query = string.Join(' ', (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var errors = new QueryErrors()
            .Require(query.Length is >= MinQueryLength and <= MaxQueryLength, nameof(q),
                $"Must be between {MinQueryLength} and {MaxQueryLength} characters.")
            .Require(lat is null == lon is null, nameof(lat), "Give both `lat` and `lon`, or neither.")
            .Require(lat is null or (>= -90 and <= 90), nameof(lat), "Must be between -90 and 90.")
            .Require(lon is null or (>= -180 and <= 180), nameof(lon), "Must be between -180 and 180.")
            .Require(lang is null || Languages.Contains(lang), nameof(lang), "Must be fr or en.");
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        try
        {
            var near = lat is { } latitude && lon is { } longitude ? new LatLon(latitude, longitude) : (LatLon?)null;
            var places = await photon.SearchAsync(new PhotonSearch(query, lang ?? "fr", near, Places.SearchLimit), cancellationToken);
            return TypedResults.Ok(Places.Suggestions(places, query).Select(s => ToSuggestion(s.Place, s.Label)).ToList());
        }
        catch (PhotonException ex)
        {
            return Failed(loggers, ex);
        }
    }

    private static async Task<Results<Ok<PlaceSuggestion>, ValidationProblem, ProblemHttpResult>> Reverse(
        PhotonClient photon,
        ILoggerFactory loggers,
        [Description("Latitude of the point, in degrees.")] double lat,
        [Description("Longitude of the point, in degrees.")] double lon,
        CancellationToken cancellationToken,
        [Description("fr (the default) or en: the language of the names and addresses.")] string? lang = null)
    {
        var errors = new QueryErrors()
            .Require(lat is >= -90 and <= 90, nameof(lat), "Must be between -90 and 90.")
            .Require(lon is >= -180 and <= 180, nameof(lon), "Must be between -180 and 180.")
            .Require(lang is null || Languages.Contains(lang), nameof(lang), "Must be fr or en.");
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var point = new LatLon(lat, lon);
        if (!QuebecArea.Contains(point))
        {
            return OutsideQuebec();
        }

        try
        {
            var places = await photon.ReverseAsync(point, lang ?? "fr", Places.ReverseRadiusKm, ReverseLimit, cancellationToken);

            // Near the border, an index with the neighbours' places could name the point after one of them.
            return Places.Nearest(point, [.. places.Where(p => QuebecArea.IsQuebec(p.State))]) is { } nearest
                ? TypedResults.Ok(ToSuggestion(nearest.Place, nearest.Label))
                : TypedResults.Problem("Nothing with an address is near this point.", statusCode: StatusCodes.Status404NotFound,
                    title: "No address here");
        }
        catch (PhotonException ex)
        {
            return Failed(loggers, ex);
        }
    }

    /// <summary>422: a point past Québec's boundary, which trips don't go to either.</summary>
    internal static ProblemHttpResult OutsideQuebec() =>
        TypedResults.Problem("This point is outside Québec.", statusCode: StatusCodes.Status422UnprocessableEntity, title: "Outside Québec");

    private static PlaceSuggestion ToSuggestion(PhotonPlace place, PlaceLabel label) =>
        new(label.Label, label.Detail, Math.Round(place.Location.Latitude, 6), Math.Round(place.Location.Longitude, 6), label.Kind);

    private static ProblemHttpResult Failed(ILoggerFactory loggers, PhotonException ex)
    {
        loggers.CreateLogger(GeocodingLog.Category).LogPhotonFailure(ex);
        return ex.Failure == PhotonFailure.Unavailable
            ? TypedResults.Problem(
                "The address search doesn't answer; try again in a few minutes.", statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Address search is unavailable")
            : TypedResults.Problem("The address search refused the request.", statusCode: StatusCodes.Status500InternalServerError);
    }
}
