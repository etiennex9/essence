using System.Globalization;
using Essence.Contracts;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;

namespace Essence.UI.App.Widgets;

/// <summary>
/// What the Android app's home-screen widgets show, asked the way the page asks: the map's fuel, the car being driven
/// (CarProfiles), the saved places and favorites, all read from the app's preferences and never written. Best near a place
/// and Best on a frequent route send the same questions as Best near you and Best on the way (<see cref="BestQuery.For"/>,
/// <see cref="TripQuery.For"/>). The texts are ready to show, in the UI's language.
/// </summary>
public sealed class WidgetFeeds(IPreferenceStore preferences, EssenceApiClient api, IStringLocalizer<Strings> l, TimeProvider time)
{
    /// <summary>The favorites widget's rows: more wouldn't fit a home screen.</summary>
    public const int MaxFavorites = 5;

    /// <summary>The favorites asked about at once.</summary>
    private const int Parallel = 4;

    /// <summary>The station Best near you would pick from <paramref name="origin"/>, with what it saves.</summary>
    public async Task<WidgetView> BestNearAsync(WidgetPlaceRef origin, CancellationToken cancellationToken)
    {
        var (fuel, car, places) = await LoadAsync();
        var title = origin.IsLastUsed ? l["BestNearYou"] : l["WidgetBestNear", PlaceName(origin, places)];
        const string tap = "/?view=best";
        if (await PointOfAsync(origin, places) is not { } point)
        {
            return Problem(title, fuel, origin.IsLastUsed ? WidgetProblem.NoLocationYet : WidgetProblem.NoPlace, tap);
        }

        var result = await api.GetBestAsync(BestQuery.For(point, fuel, car), cancellationToken);
        if (!result.IsSuccess || result.Value is not { } best)
        {
            return Problem(title, fuel, WidgetProblem.Failed, tap);
        }

        if (best.Pick is not { } pick)
        {
            return Problem(title, fuel, WidgetProblem.NoPrice, tap);
        }

        var culture = CultureInfo.CurrentCulture;
        var saving = pick.Id == best.ReferenceId || pick.SavesDollars <= 0
            ? l["Closest"].Value
            : l["Saves", Formatting.Money(pick.SavesDollars, culture)].Value;
        var distance = l[best.ByRoad ? "DistanceByRoad" : "DistanceApprox", Formatting.Km(pick.DistanceKm, culture)];
        var detail = $"{distance} · {saving}";
        return View(title, fuel, [Line(pick.Id, pick.Brand, detail, pick.PriceCents, $"/?station={pick.Id}")], tap);
    }

    /// <summary>The starred stations with their price of the fuel: cheapest first, those without one after, in list order.</summary>
    public async Task<WidgetView> FavoritesAsync(CancellationToken cancellationToken)
    {
        var (fuel, _, _) = await LoadAsync();
        var title = l["Favorites"].Value;
        const string tap = "/?view=favorites";
        var favorites = new FavoriteStations(preferences);
        await favorites.LoadAsync();
        if (favorites.Count == 0)
        {
            return Problem(title, fuel, WidgetProblem.NoFavorites, tap);
        }

        // The first ones starred, as many as could show if all have a price: asking for all of them would be a lot of requests.
        var asked = favorites.Favorites.Take(MaxFavorites * 4).ToList();
        var details = new ApiResult<StationDetail>[asked.Count];
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(asked.Select(async (station, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                details[i] = await api.GetStationAsync(station.Id, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));

        // One unanswered is a failure too: shown without its price, it would pass for a station without one and sort last.
        if (details.Any(d => d.Error is not (ApiError.None or ApiError.NotFound)))
        {
            return Problem(title, fuel, WidgetProblem.Failed, tap);
        }

        var lines = asked
            .Select((station, order) => (Station: station, Order: order, Result: details[order]))
            // A station the feed dropped is gone from the widget, as from the page's list.
            .Where(r => r.Result.Error != ApiError.NotFound)
            .Select(r =>
            {
                var price = r.Result.Value?.Prices.FirstOrDefault(p => string.Equals(p.Fuel, fuel, StringComparison.OrdinalIgnoreCase));
                var cents = price is { IsAvailable: true, PriceCents: { } c } ? c : (decimal?)null;
                return (r.Station, r.Order, Cents: cents);
            })
            .OrderBy(r => r.Cents ?? decimal.MaxValue)
            .ThenBy(r => r.Order)
            .Take(MaxFavorites)
            .Select(r => Line(r.Station.Id, r.Station.Brand, r.Station.Address, r.Cents, $"/?station={r.Station.Id}"))
            .ToList();
        return lines.Count == 0 ? Problem(title, fuel, WidgetProblem.NoFavorites, tap) : View(title, fuel, lines, tap);
    }

    /// <summary>
    /// The station Best on the way would pick from <paramref name="from"/> to <paramref name="to"/>. The range is left out: a
    /// widget shows for hours, longer than a dashboard reading lasts (and the API doesn't count it under 100 km).
    /// </summary>
    public async Task<WidgetView> TripAsync(WidgetPlaceRef? from, WidgetPlaceRef? to, CancellationToken cancellationToken)
    {
        var (fuel, car, places) = await LoadAsync();
        if (from is null || to is null)
        {
            return Problem(l["BestOnTheWay"], fuel, WidgetProblem.NoPlace, "/?view=places");
        }

        var title = l["WidgetRoute", PlaceName(from, places), PlaceName(to, places)].Value;
        var tap = TripLink(from, to, places, pick: null);
        if (await PointOfAsync(from, places) is not { } start)
        {
            return Problem(title, fuel, from.IsLastUsed ? WidgetProblem.NoLocationYet : WidgetProblem.NoPlace, tap);
        }

        if (to.Find(places) is not { } destination)
        {
            return Problem(title, fuel, WidgetProblem.NoPlace, "/?view=places");
        }

        var result = await api.GetTripAsync(TripQuery.For(start, destination.Point, fuel, car, rangeKm: null), cancellationToken);
        if (!result.IsSuccess || result.Value is not { } plan)
        {
            return Problem(title, fuel, result.Error switch
            {
                ApiError.NoRoute => WidgetProblem.NoRoute,
                ApiError.Unavailable => WidgetProblem.Unavailable,
                _ => WidgetProblem.Failed,
            }, tap);
        }

        if (plan.Stations.FirstOrDefault(s => s.Id == plan.PickId) is not { } pick)
        {
            return Problem(title, fuel, WidgetProblem.NoPrice, tap);
        }

        var culture = CultureInfo.CurrentCulture;
        var where = pick.DetourKm <= Components.BestPanel.NoDetourKm && pick.DetourMin <= Components.BestPanel.NoDetourMinutes
            ? l["NoDetour"].Value
            : l["DetourMinutesShort", Math.Round(pick.DetourMin)].Value;
        var detail = pick.Id == plan.ReferenceId || pick.SavesDollars <= 0
            ? where
            : $"{where} · {l["Saves", Formatting.Money(pick.SavesDollars, culture)]}";
        var line = TripLink(from, to, places, pick.Id);
        return View(title, fuel, [Line(pick.Id, pick.Brand, detail, pick.PriceCents, line)], line);
    }

    /// <summary>
    /// Opens the trip the widget shows: the same two places (from where the user is, for the last-used place), and the station
    /// it showed on top (MapPage's <c>to</c>, <c>from</c> and <c>pick</c>). With a place forgotten, Your places.
    /// </summary>
    private static string TripLink(WidgetPlaceRef from, WidgetPlaceRef to, SavedPlaces places, long? pick)
    {
        if (to.LinkName is not { } toName || to.Find(places) is null || (!from.IsLastUsed && (from.LinkName is null || from.Find(places) is null)))
        {
            return "/?view=places";
        }

        var link = $"/?to={Uri.EscapeDataString(toName)}";
        if (!from.IsLastUsed)
        {
            link += $"&from={Uri.EscapeDataString(from.LinkName!)}";
        }

        return pick is { } id ? link + string.Create(CultureInfo.InvariantCulture, $"&pick={id}") : link;
    }

    /// <summary>A widget's message when the app has no server to ask yet.</summary>
    public WidgetView NoServer(string title) => Problem(title, Fuels.Default, WidgetProblem.NoServer, "/");

    /// <summary>The message for a problem, in the UI's language.</summary>
    public string MessageFor(WidgetProblem problem, string fuel) => problem switch
    {
        WidgetProblem.NoServer => l["WidgetNoServer"],
        WidgetProblem.NoPlace => l["WidgetNoPlace"],
        WidgetProblem.NoLocationYet => l["WidgetNoLocationYet"],
        WidgetProblem.NoFavorites => l["WidgetNoFavorites"],
        WidgetProblem.NoPrice => l["WidgetNoPrice", fuel],
        WidgetProblem.NoRoute => l["WidgetNoRoute"],
        WidgetProblem.Unavailable => l["WidgetUnavailable"],
        WidgetProblem.Failed => l["WidgetFailed"],
        _ => "",
    };

    /// <summary>How a place is named on a widget: Home, Work, its own name, or "you" for where the app was last used.</summary>
    public string PlaceName(WidgetPlaceRef place, SavedPlaces places) => place.Kind switch
    {
        PlaceKind.Home => l["Home"],
        PlaceKind.Work => l["Work"],
        _ when place.IsLastUsed => l["WidgetYou"],
        _ => place.Find(places) is { Name: { Length: > 0 } name } ? name : l["WidgetPlace"],
    };

    private async Task<(string Fuel, CarProfile Car, SavedPlaces Places)> LoadAsync()
    {
        var fuel = Fuels.Normalize(await preferences.GetAsync(Fuels.PreferenceKey));
        var cars = new CarProfiles(preferences);
        await cars.LoadAsync();
        var places = new SavedPlaces(preferences);
        await places.LoadAsync();
        return (fuel, cars.Active, places);
    }

    private async Task<GeoPoint?> PointOfAsync(WidgetPlaceRef place, SavedPlaces places) =>
        place.IsLastUsed ? (await LastLocation.ReadAsync(preferences))?.Point : place.Find(places)?.Point;

    private WidgetLine Line(long id, string? brand, string detail, decimal? cents, string tap)
    {
        var style = BrandCatalog.For(brand);
        return new WidgetLine(id, style.Name ?? l["Independent"], detail, Formatting.Price(cents, CultureInfo.CurrentCulture), style, tap);
    }

    private WidgetView View(string title, string fuel, IReadOnlyList<WidgetLine> lines, string tap) =>
        new(title, fuel, lines, WidgetProblem.None, null, tap, Updated());

    private WidgetView Problem(string title, string fuel, WidgetProblem problem, string tap) =>
        new(title, fuel, [], problem, MessageFor(problem, fuel), tap, Updated());

    private string Updated() =>
        l["WidgetUpdated", Formatting.TimeOfDay(time.GetUtcNow(), time.LocalTimeZone, CultureInfo.CurrentCulture)];
}
