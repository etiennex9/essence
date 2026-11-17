using Essence.Data;
using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>
/// Stamps a response with the version of the data behind it: when the latest successful import was fetched, as
/// <c>Last-Modified</c>. The map times its next refresh from it (the next import lands about 5 minutes later), and a
/// repeated request whose <c>If-Modified-Since</c> is still current gets a 304 without running the handler. The stations'
/// opening hours change apart from the feed, once a week: <c>Last-Modified</c> stays the feed's, so the map's "prices as
/// of" and its refreshes don't move, but no 304 is given for a version older than when the hours last changed
/// (<see cref="Snapshot.HoursChangedAt"/>).
/// </summary>
public static class DataVersion
{
    private static readonly object UncachedKey = new();

    public static RouteHandlerBuilder WithDataVersion(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(FilterAsync).Produces(StatusCodes.Status304NotModified);

    /// <summary>
    /// This answer isn't the data's alone (Best near you estimated while the routing server was down): it's sent without
    /// the version and marked <c>no-store</c>, so the same request asks again instead of getting a 304 for it.
    /// </summary>
    public static void DontCache(HttpContext http) => http.Items[UncachedKey] = true;

    /// <summary>
    /// Whether a client that has the data as of <paramref name="ifModifiedSince"/> has it still: the latest import is no
    /// newer, and the hours didn't change after it either. When they changed is rounded up to the second: a client whose
    /// <c>If-Modified-Since</c> names the second they changed in may not have the change.
    /// </summary>
    /// <param name="hoursChangedAt">When the hours last changed (<see cref="Snapshot.HoursChangedAt"/>); null if never.</param>
    public static bool IsNotModified(DateTimeOffset? ifModifiedSince, DateTimeOffset version, DateTimeOffset? hoursChangedAt) =>
        ifModifiedSince >= version && !(UpToTheSecond(hoursChangedAt) > ifModifiedSince);

    private static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Read before the handler: an import committing in between makes the data newer than its version, which costs the
        // client one extra refresh, never a missed one. The snapshot's, which the handlers read too: it knows when the hours
        // last changed, a source gone stale with no run included.
        var db = http.RequestServices.GetRequiredService<EssenceDbContext>();
        var snapshot = await http.RequestServices.GetRequiredService<StationSnapshot>().GetAsync(db, http.RequestAborted);
        if (snapshot.Version is not { } version)
        {
            return await next(context);
        }

        if (IsNotModified(http.Request.GetTypedHeaders().IfModifiedSince, version, snapshot.HoursChangedAt))
        {
            SetHeaders(http.Response, version);
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        // Only a 200 carries the version: a 400 or 404 isn't data to cache.
        http.Response.OnStarting(() =>
        {
            if (http.Items.ContainsKey(UncachedKey))
            {
                http.Response.Headers.CacheControl = "no-store";
            }
            else if (http.Response.StatusCode == StatusCodes.Status200OK)
            {
                SetHeaders(http.Response, version);
            }

            return Task.CompletedTask;
        });
        return await next(context);
    }

    /// <summary>The fetch time of the latest version imported, to the second like HTTP dates; null before the first import.</summary>
    internal static async Task<DateTimeOffset?> LatestAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        var fetchedAt = await db.Imports
            .Where(i => i.Status == ImportStatus.Succeeded)
            .OrderByDescending(i => i.GeneratedAt)
            .Select(i => (DateTimeOffset?)i.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return fetchedAt?.AddTicks(-(fetchedAt.Value.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// When the latest hours run that changed the hours served finished, as stored: one that wrote rows
    /// (<see cref="HoursImportStatus.Succeeded"/>, <see cref="HoursImportStatus.Rematched"/>, or
    /// <see cref="HoursImportStatus.Disabled"/> with rows removed), or a success that wrote none
    /// (<see cref="HoursImportStatus.Unchanged"/>), which can make a stale source's hours count again. Not rounded: the runs
    /// take turns (the ingest's advisory lock), so each one moves it, even two in the same second. Null before the first.
    /// </summary>
    internal static async Task<DateTimeOffset?> HoursLatestAsync(EssenceDbContext db, CancellationToken cancellationToken) =>
        await db.HoursImports
            .Where(i => i.Status == HoursImportStatus.Succeeded
                || i.Status == HoursImportStatus.Unchanged
                || i.Status == HoursImportStatus.Rematched
                || (i.Status == HoursImportStatus.Disabled && i.Changed > 0))
            .MaxAsync(i => (DateTimeOffset?)i.FinishedAt, cancellationToken);

    private static DateTimeOffset? UpToTheSecond(DateTimeOffset? at) =>
        at is { } value && value.Ticks % TimeSpan.TicksPerSecond is var part and > 0
            ? value.AddTicks(TimeSpan.TicksPerSecond - part)
            : at;

    /// <summary><c>no-cache</c>: a browser may keep the response, but must check it's still current before using it again.</summary>
    private static void SetHeaders(HttpResponse response, DateTimeOffset version)
    {
        response.GetTypedHeaders().LastModified = version;
        response.Headers.CacheControl = "no-cache";
    }
}
