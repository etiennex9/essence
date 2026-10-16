using Essence.Data;
using Essence.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Essence.Api;

/// <summary>
/// Stamps a response with the version of the data behind it: when the latest successful import was fetched, as
/// <c>Last-Modified</c>. The map times its next refresh from it (the next import lands about 5 minutes later), and a
/// repeated request whose <c>If-Modified-Since</c> is still current gets a 304 without running the handler.
/// </summary>
public static class DataVersion
{
    public static RouteHandlerBuilder WithDataVersion(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(FilterAsync).Produces(StatusCodes.Status304NotModified);

    private static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Read before the handler: an import committing in between makes the data newer than its version, which costs the
        // client one extra refresh, never a missed one.
        var db = http.RequestServices.GetRequiredService<EssenceDbContext>();
        if (await LatestAsync(db, http.RequestAborted) is not { } version)
        {
            return await next(context);
        }

        if (http.Request.GetTypedHeaders().IfModifiedSince >= version)
        {
            SetHeaders(http.Response, version);
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        // Only a 200 carries the version: a 400 or 404 isn't data to cache.
        http.Response.OnStarting(() =>
        {
            if (http.Response.StatusCode == StatusCodes.Status200OK)
            {
                SetHeaders(http.Response, version);
            }

            return Task.CompletedTask;
        });
        return await next(context);
    }

    /// <summary>The fetch time of the latest version imported, to the second like HTTP dates; null before the first import.</summary>
    private static async Task<DateTimeOffset?> LatestAsync(EssenceDbContext db, CancellationToken cancellationToken)
    {
        var fetchedAt = await db.Imports
            .Where(i => i.Status == ImportStatus.Succeeded)
            .OrderByDescending(i => i.GeneratedAt)
            .Select(i => (DateTimeOffset?)i.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return fetchedAt?.AddTicks(-(fetchedAt.Value.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary><c>no-cache</c>: a browser may keep the response, but must check it's still current before using it again.</summary>
    private static void SetHeaders(HttpResponse response, DateTimeOffset version)
    {
        response.GetTypedHeaders().LastModified = version;
        response.Headers.CacheControl = "no-cache";
    }
}
