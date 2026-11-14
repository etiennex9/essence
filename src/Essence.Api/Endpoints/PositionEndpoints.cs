using Essence.Contracts;
using Essence.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api.Endpoints;

public static class PositionEndpoints
{
    public static IEndpointRouteBuilder MapPositionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stations").WithTags("Stations").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/positions", Positions)
            .WithSummary("Stations the Régie's feed puts in the wrong place: the corrections, and the stations to review")
            .WithDescription(
                "`corrections` lists each reviewed correction of `curated-positions.json` (README → Position corrections): the "
                + "station (`stationId`, `name`, `address`; null when no station has its `naturalKey` any more), where the Régie "
                + "puts it now (`feed`), where the correction puts it (`corrected`), how far apart (`distanceM`), and its `state`: "
                + "`applied` (every answer gives the corrected point), `expired` (the Régie's point moved more than 50 m since the "
                + "review, or to within 50 m of the corrected one: the Régie's is used again) or `station gone`; with `reviewed` "
                + "and the evidence (`note`). `candidates` lists the stations the ingest found once a day where two independent "
                + "kinds of position (`osm`: an OpenStreetMap fuel element at the station's address, of its brand or none; "
                + "`locator`: a brand's locator, by the store number in the station's name or at its address; `photon`: an "
                + "OpenStreetMap address for the station's) agree within 75 m of each other, both more than 150 m from the "
                + "Régie's point, with nothing of the station's brand within 75 m of that point; farthest first, each with "
                + "the point `proposed` (their mean), its `evidence`, and the `entry` to paste into the file once checked. "
                + "Nothing moves a station until an entry is in the file. `warnings` says which entries of the file were left "
                + "out, and why.");
        return app;
    }

    private static async Task<Ok<StationPositions>> Positions(EssenceDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        TypedResults.Ok(await PositionReview.LoadAsync(db, PositionCorrections.Embedded, PositionReview.Today(time.GetUtcNow()), cancellationToken));
}
