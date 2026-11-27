using System.ComponentModel;
using Essence.Contracts;
using Essence.Data;
using Essence.Hours;
using Essence.Ranking;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Essence.Api.Endpoints;

public static class TrendEndpoints
{
    internal const string TrendsDescription =
        "The area is the one Best near you looks at for the fuel: the operating stations with an available price within "
        + "`radiusKm`, widened to 25 km with fewer than 5 (Costco with `costco` only). Each is read from the price history "
        + "of the last 56 days, a price that lasted under 20 minutes aside (a correction), and only at moments the data "
        + "covers: within 10 minutes of an import. `now` is as of the latest import; `yesterday` and `lastWeek` compare the "
        + "median at the same time of day, on the area's clock, of the stations priced both times. `low` is the lowest price "
        + "of the last 24 hours; `last24h` samples the lowest and median prices every 15 minutes. `jump` is a rise going "
        + "through the area: a quarter of the stations with a price (3 at least, of 8 at least) up 5 ¢ or more within 3 "
        + "hours, each against its low of the 4 hours before. It ends 45 minutes after the last rise of a station that "
        + "usually follows the area's rises (one that joined 60 % of at least 8 of them in the last 28 days; of any station "
        + "where none is known to, or none of the rises that started it is a follower's), 3 hours after it started at most, "
        + "and isn't given once the data is more than 10 minutes old. `notRaised` lists the stations that usually follow, "
        + "haven't risen, are within 1 ¢ of their low and changed their price in the last 2 days. `pattern` is that of the "
        + "same area around the centre of the 1 km cell the point is in, computed once a day: when the rises usually come "
        + "(within an hour of one time on 60 % of the days with data of the last 28, 7 at least, 10 days at least), the "
        + "hours prices are usually lowest (when the day's swing is 3 ¢ or more and regular, over the last 28 days), and the "
        + "two days of the week they're usually lowest (1.5 ¢ or more below the week around them on average, and below it "
        + "on 75 % of the weeks, over 6 weeks at least of the history kept). No 304 once the data is more than 10 minutes "
        + "old: the answer depends on the time.";

    public static IEndpointRouteBuilder MapTrendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stations").WithTags("Stations").RequireRateLimiting(RateLimiting.PolicyName);
        group.MapGet("/trends", Trends)
            .WithSummary("How prices are moving around a point")
            .WithDescription(TrendsDescription + " " + StationEndpoints.PositionDescription)
            .WithDataVersion(clockDependent: true);
        return app;
    }

    private static async Task<Results<Ok<PriceTrends>, ValidationProblem>> Trends(
        EssenceDbContext db,
        StationSnapshot snapshot,
        PriceTimeline timeline,
        TrendPatterns patterns,
        TimeProvider time,
        [Description("Latitude of the point, in degrees.")] double lat,
        [Description("Longitude of the point, in degrees.")] double lon,
        CancellationToken cancellationToken,
        [Description("Régulier, Super or Diesel (accents optional). Required.")] string? fuel = null,
        [Description("true to include Costco, which sells to members only.")] bool costco = false,
        [Description("Search radius in kilometres (max 25).")] double radiusKm = SearchArea.DefaultRadiusKm)
    {
        var errors = new QueryErrors()
            .Require(lat is >= -90 and <= 90, nameof(lat), "Must be between -90 and 90.")
            .Require(lon is >= -180 and <= 180, nameof(lon), "Must be between -180 and 180.")
            .Require(!string.IsNullOrWhiteSpace(fuel), nameof(fuel), "Required.")
            .Require(radiusKm is > 0 and <= SearchArea.MaxRadiusKm, nameof(radiusKm), $"Must be more than 0 and at most {SearchArea.MaxRadiusKm}.");
        var fuelType = await StationEndpoints.ResolveFuelAsync(db, fuel, errors, cancellationToken);
        if (errors.Any || fuelType is null)
        {
            return errors.ToProblem();
        }

        var current = await snapshot.GetAsync(db, cancellationToken);
        var window = await timeline.GetAsync(db, current.Version, cancellationToken);
        var stations = StationEndpoints.Eligible(current.Stations, fuelType.Id, costco, lat, lon);
        var (area, areaRadiusKm) = SearchArea.Select(stations, s => s.Km, radiusKm);
        var now = time.GetUtcNow();
        var zone = QuebecTime.ZoneFor(lat, lon);
        var prices = AreaOf(area.Select(s => s.Station.Id), window, fuelType.Id);
        var figures = Ranking.Trends.Compute(prices, window.Coverage, now, zone);
        if (figures is null)
        {
            return TypedResults.Ok(new PriceTrends(fuelType.Code, null, areaRadiusKm, area.Count, null, null, null, null, null, null, null));
        }

        var pattern = patterns.Get(fuelType.Id, costco, radiusKm, lat, lon, current, window, figures.AsOf);
        var jump = IsLate(now, current.Version) ? null : Jumps.Detect(prices, window.Coverage, figures.AsOf.UtcTicks, pattern?.Followers);
        var byId = area.ToDictionary(s => s.Station.Id, s => s.Station);
        return TypedResults.Ok(new PriceTrends(
            fuelType.Code,
            figures.AsOf,
            areaRadiusKm,
            area.Count,
            figures.Now is { } sample ? new AreaPrices(figures.AsOf, sample.LowestCents, sample.MedianCents, sample.Stations) : null,
            ToChange(figures.Yesterday),
            ToChange(figures.LastWeek),
            figures.Low is { } low
                ? new AreaLow(low.StationId, byId[low.StationId].Name, byId[low.StationId].Brand, low.PriceCents, low.From, low.To, low.StillNow)
                : null,
            new AreaSeries(
                figures.SeriesFrom,
                (int)TrendRules.Step.TotalMinutes,
                [.. figures.Series.Select(s => s?.LowestCents)],
                [.. figures.Series.Select(s => s?.MedianCents)]),
            jump is null ? null : ToJump(jump, pattern, zone),
            ToPattern(pattern)));
    }

    /// <summary>The area's stations with their prices for the fuel in the history; those without any left out.</summary>
    internal static List<AreaStation> AreaOf(IEnumerable<long> stationIds, PriceWindow window, short fuelTypeId) =>
        [.. stationIds.Select(id => window.Of(id, fuelTypeId) is { } prices ? new AreaStation(id, prices) : null).OfType<AreaStation>()];

    /// <summary>
    /// Whether the data is late: the latest import (<paramref name="version"/>, <see cref="Snapshot.Version"/>) is more than
    /// <see cref="DataVersion.ClockDependentFor"/> old, past which no 304 is given either. A rise isn't told on late data.
    /// </summary>
    public static bool IsLate(DateTimeOffset now, DateTimeOffset? version) =>
        version is not { } latest || now - latest > DataVersion.ClockDependentFor;

    /// <summary>Whether a jump came at about the usual time: within <see cref="TrendRules.UsualWithin"/> of it, on the area's clock.</summary>
    public static bool IsUsual(DateTimeOffset since, TimeOnly? usual, TimeZoneInfo zone)
    {
        if (usual is not { } around)
        {
            return false;
        }

        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(since, zone).DateTime);
        var apart = Math.Abs((local - around).TotalMinutes);
        return Math.Min(apart, 1440 - apart) <= TrendRules.UsualWithin.TotalMinutes;
    }

    private static AreaChange? ToChange(AreaComparison? comparison) =>
        comparison is null ? null : new AreaChange(comparison.At, comparison.MedianCents, comparison.NowMedianCents, comparison.Stations);

    private static PriceJump ToJump(JumpState jump, AreaPattern? pattern, TimeZoneInfo zone) =>
        new(
            jump.Since,
            jump.Until,
            jump.Raised,
            jump.Stations,
            jump.RiseCents,
            jump.BeforeCents,
            jump.AfterCents,
            [.. jump.NotRaised.Select(h => new NotRaisedStation(h.StationId, h.PriceCents))],
            IsUsual(jump.Since, pattern?.JumpsAround, zone));

    private static DailyPattern? ToPattern(AreaPattern? pattern) =>
        pattern is null || (pattern.JumpsAround is null && pattern.LowestFromHour is null && pattern.CheapestDays is null)
            ? null
            : new DailyPattern(pattern.JumpsAround, pattern.LowestFromHour, pattern.LowestToHour, pattern.Days, pattern.CheapestDays, pattern.Weeks);
}
