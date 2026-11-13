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
/// <see cref="TripQuery.For"/>). Where you are is the device's latest fix (<see cref="LastLocation"/>), whose time shows once
/// it's old; <c>access</c> and <c>locationOn</c> (the device's location setting) only say what to ask for when there's none,
/// or why it can't be updated. The texts are ready to show, in the UI's language.
/// </summary>
public sealed class WidgetFeeds(
    IPreferenceStore preferences,
    EssenceApiClient api,
    IStringLocalizer<Strings> l,
    TimeProvider time,
    WidgetLocationAccess access = WidgetLocationAccess.WhileInUse,
    bool locationOn = true)
{
    /// <summary>The favorites widget's rows: more wouldn't fit a home screen.</summary>
    public const int MaxFavorites = 5;

    /// <summary>The favorites asked about at once.</summary>
    private const int Parallel = 4;

    /// <summary>
    /// A favorite closing within this of the refresh says when: the widget refreshes every 30 minutes, and its text stays
    /// until the next refresh, or longer when Android holds it back.
    /// </summary>
    private static readonly TimeSpan ClosesSoonOnWidget = TimeSpan.FromMinutes(90);

    private readonly StationHoursView _hours = new(l);

    /// <summary>The station Best near you would pick from <paramref name="origin"/>, with what it saves.</summary>
    public async Task<WidgetView> BestNearAsync(WidgetPlaceRef origin, CancellationToken cancellationToken)
    {
        var (fuel, car, places) = await LoadAsync();
        var title = origin.IsYou ? l["BestNearYou"] : l["WidgetBestNear", PlaceName(origin, places)];
        const string tap = "/?view=best";
        if (await StartOfAsync(origin, places) is not { } start)
        {
            return Problem(title, fuel, NotFound(origin), tap);
        }

        var result = await api.GetBestAsync(BestQuery.For(start.Point, fuel, car), cancellationToken);
        if (!result.IsSuccess || result.Value is not { } best)
        {
            return Problem(title, fuel, WidgetProblem.Failed, tap, start.Fix);
        }

        if (best.Pick is not { } pick)
        {
            return Problem(title, fuel, WidgetProblem.NoPrice, tap, start.Fix);
        }

        var culture = CultureInfo.CurrentCulture;
        var saving = pick.Id == best.ReferenceId || pick.SavesDollars <= 0
            ? l["Closest"].Value
            : l["Saves", Formatting.Money(pick.SavesDollars, culture)].Value;
        var distance = l[best.ByRoad ? "DistanceByRoad" : "DistanceApprox", Formatting.Km(pick.DistanceKm, culture)];
        var detail = WithHours($"{distance} · {saving}", ArrivalNote(pick.Hours, pick.Latitude, pick.Longitude, pick.EtaMin));
        return View(title, fuel, [Line(pick.Id, pick.Brand, detail, pick.PriceCents, $"/?station={pick.Id}")], tap, start.Fix);
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

        var now = time.GetUtcNow();
        var lines = asked
            .Select((station, order) => (Station: station, Order: order, Result: details[order]))
            // A station the feed dropped is gone from the widget, as from the page's list.
            .Where(r => r.Result.Error != ApiError.NotFound)
            .Select(r =>
            {
                var price = r.Result.Value?.Prices.FirstOrDefault(p => string.Equals(p.Fuel, fuel, StringComparison.OrdinalIgnoreCase));
                var cents = price is { IsAvailable: true, PriceCents: { } c } ? c : (decimal?)null;
                return (r.Station, r.Order, Cents: cents, Hours: HoursNote(r.Result.Value, now));
            })
            .OrderBy(r => r.Cents ?? decimal.MaxValue)
            .ThenBy(r => r.Order)
            .Take(MaxFavorites)
            .Select(r => Line(
                r.Station.Id, r.Station.Brand, r.Hours is { } hours ? $"{hours} · {r.Station.Address}" : r.Station.Address, r.Cents,
                $"/?station={r.Station.Id}"))
            .ToList();
        return lines.Count == 0 ? Problem(title, fuel, WidgetProblem.NoFavorites, tap) : View(title, fuel, lines, tap);
    }

    /// <summary>
    /// What a favorite's line says of its hours at the refresh: "Closes 9:30 p.m." when it closes within
    /// <see cref="ClosesSoonOnWidget"/>, "Closed · opens 6:00 a.m." while it's closed; nothing otherwise. It goes before the
    /// address, which the line cuts short first.
    /// </summary>
    private string? HoursNote(StationDetail? station, DateTimeOffset now)
    {
        if (station is null)
        {
            return null;
        }

        var view = _hours.Describe(station.Hours, station.Latitude, station.Longitude, now);
        return view.IsClosed ? view.Status
            : view.ClosesAt - now <= ClosesSoonOnWidget ? view.Closes
            : null;
    }

    /// <summary>
    /// What the pick's line on Best near a place and Best on a frequent route says of its hours, as a favorite's
    /// (<see cref="HoursNote"/>), but when the driver gets there, <paramref name="etaMinutes"/> after the refresh: "Closed ·
    /// opens 6:00 a.m." while it's closed now and still then; "Closes 9:30 p.m." when it closes before the driver gets there,
    /// or open then, within <see cref="ClosesSoonOnWidget"/> of the refresh; nothing otherwise.
    /// </summary>
    private string? ArrivalNote(StationHours? hours, double latitude, double longitude, double? etaMinutes)
    {
        var now = time.GetUtcNow();
        var arrival = _hours.Arrive(hours, latitude, longitude, now, etaMinutes, name: "");
        return arrival.Kind switch
        {
            ArrivalKind.Closed or ArrivalKind.TemporarilyClosed => _hours.Describe(hours, latitude, longitude, now).Status,
            ArrivalKind.ClosesBefore => _hours.Describe(hours, latitude, longitude, now).Closes,
            ArrivalKind.Open or ArrivalKind.ClosesSoon when arrival.ClosesAt - now <= ClosesSoonOnWidget =>
                _hours.Describe(hours, latitude, longitude, arrival.ArriveAt).Closes,
            _ => null,
        };
    }

    /// <summary>The detail with the hours' note after it, when there's one.</summary>
    private static string WithHours(string detail, string? note) => note is null ? detail : $"{detail} · {note}";

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
        if (await StartOfAsync(from, places) is not { } start)
        {
            return Problem(title, fuel, NotFound(from), tap);
        }

        if (to.Find(places) is not { } destination)
        {
            return Problem(title, fuel, WidgetProblem.NoPlace, "/?view=places");
        }

        // No trip to plan: Best near you is what's left to open.
        if (start.Fix is { } fix && WidgetLocation.IsThere(fix, destination.Point))
        {
            return Problem(title, fuel, WidgetProblem.AtDestination, "/?view=best", fix);
        }

        var result = await api.GetTripAsync(TripQuery.For(start.Point, destination.Point, fuel, car, rangeKm: null), cancellationToken);
        if (!result.IsSuccess || result.Value is not { } plan)
        {
            return Problem(title, fuel, result.Error switch
            {
                ApiError.NoRoute => WidgetProblem.NoRoute,
                ApiError.Unavailable => WidgetProblem.Unavailable,
                _ => WidgetProblem.Failed,
            }, tap, start.Fix);
        }

        if (plan.Stations.FirstOrDefault(s => s.Id == plan.PickId) is not { } pick)
        {
            return Problem(title, fuel, WidgetProblem.NoPrice, tap, start.Fix);
        }

        var culture = CultureInfo.CurrentCulture;
        var where = pick.DetourKm <= Components.BestPanel.NoDetourKm && pick.DetourMin <= Components.BestPanel.NoDetourMinutes
            ? l["NoDetour"].Value
            : l["DetourMinutesShort", Math.Round(pick.DetourMin)].Value;
        var detail = pick.Id == plan.ReferenceId || pick.SavesDollars <= 0
            ? where
            : $"{where} · {l["Saves", Formatting.Money(pick.SavesDollars, culture)]}";
        detail = WithHours(detail, ArrivalNote(pick.Hours, pick.Latitude, pick.Longitude, pick.ToMin));
        var line = TripLink(from, to, places, pick.Id);
        return View(title, fuel, [Line(pick.Id, pick.Brand, detail, pick.PriceCents, line)], line, start.Fix);
    }

    /// <summary>
    /// Opens the trip the widget shows: the same two places (from where the user is then, for where you are), and the station
    /// it showed on top (MapPage's <c>to</c>, <c>from</c> and <c>pick</c>). With a place forgotten, Your places.
    /// </summary>
    private static string TripLink(WidgetPlaceRef from, WidgetPlaceRef to, SavedPlaces places, long? pick)
    {
        if (to.LinkName is not { } toName || to.Find(places) is null || (!from.IsYou && (from.LinkName is null || from.Find(places) is null)))
        {
            return "/?view=places";
        }

        var link = $"/?to={Uri.EscapeDataString(toName)}";
        if (!from.IsYou)
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
        WidgetProblem.LocationNotAllowed => l["WidgetLocationNotAllowed"],
        WidgetProblem.AtDestination => l["WidgetAtDestination"],
        WidgetProblem.LocationOff => l["WidgetLocationOff"],
        _ => "",
    };

    /// <summary>How a place is named on a widget: Home, Work, its own name, or "you" for where you are.</summary>
    public string PlaceName(WidgetPlaceRef place, SavedPlaces places) => place.Kind switch
    {
        PlaceKind.Home => l["Home"],
        PlaceKind.Work => l["Work"],
        _ when place.IsYou => l["WidgetYou"],
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

    /// <summary>Where a widget ranks from: a saved place's point, or for where you are, the latest fix.</summary>
    private async Task<Start?> StartOfAsync(WidgetPlaceRef place, SavedPlaces places)
    {
        if (!place.IsYou)
        {
            return place.Find(places) is { } saved ? new Start(saved.Point, null) : null;
        }

        return await LastLocation.ReadAsync(preferences) is { } fix ? new Start(fix.Point, fix) : null;
    }

    /// <summary>
    /// Why there's nowhere to rank from: a forgotten place, or for where you are, no fix yet: no permission, the location off,
    /// or none found so far.
    /// </summary>
    private WidgetProblem NotFound(WidgetPlaceRef place) =>
        !place.IsYou ? WidgetProblem.NoPlace
        : access == WidgetLocationAccess.NotAllowed ? WidgetProblem.LocationNotAllowed
        : !locationOn ? WidgetProblem.LocationOff
        : WidgetProblem.NoLocationYet;

    private WidgetLine Line(long id, string? brand, string detail, decimal? cents, string tap)
    {
        var style = BrandCatalog.For(brand);
        return new WidgetLine(id, style.Name ?? l["Independent"], detail, Formatting.Price(cents, CultureInfo.CurrentCulture), style, tap);
    }

    private WidgetView View(string title, string fuel, IReadOnlyList<WidgetLine> lines, string tap, LastFix? fix = null) =>
        new(title, fuel, lines, WidgetProblem.None, null, tap, Updated(fix));

    private WidgetView Problem(string title, string fuel, WidgetProblem problem, string tap, LastFix? fix = null) =>
        new(title, fuel, [], problem, MessageFor(problem, fuel), tap, Updated(fix));

    /// <summary>
    /// "Updated 8:10 a.m.", and for where you are, when the fix is from (once it's older than one periodic refresh), whether
    /// it's approximate, and that the location is off (so ↻ can't update it). Times rather than "2 h ago": the text is written
    /// at the refresh, and a widget Android stops refreshing would keep saying 2 h.
    /// </summary>
    private string Updated(LastFix? fix)
    {
        var now = time.GetUtcNow();
        var culture = CultureInfo.CurrentCulture;
        var zone = time.LocalTimeZone;
        var updated = l["WidgetUpdated", Formatting.TimeOfDay(now, zone, culture)].Value;
        if (fix is null)
        {
            return updated;
        }

        var notes = new List<string> { updated };
        if (now - fix.At >= WidgetLocation.ShowAgeAfter)
        {
            var sameDay = TimeZoneInfo.ConvertTime(fix.At, zone).Date == TimeZoneInfo.ConvertTime(now, zone).Date;
            notes.Add(sameDay
                ? l["WidgetLocatedAt", Formatting.TimeOfDay(fix.At, zone, culture)]
                : l["WidgetLocatedOn", Formatting.MonthDay(fix.At, zone, culture)]);
        }

        if (fix.AccuracyMeters > BestOrigin.ApproximateBeyondMeters)
        {
            notes.Add(l["WidgetApproximate"]);
        }

        if (!locationOn && access != WidgetLocationAccess.NotAllowed)
        {
            notes.Add(l["WidgetLocationOffNote"]);
        }

        return string.Join(" · ", notes);
    }

    /// <summary>Where a widget ranks from; <paramref name="Fix"/> is the device's, for where you are.</summary>
    private sealed record Start(GeoPoint Point, LastFix? Fix);
}
