using Essence.Hours;

namespace Essence.Ranking.Tests;

/// <summary>
/// The stations Best near you and Best on the way leave out because they'll be closed when the driver gets there, on
/// Costco Laval's gas bar: Mo-Fr 06:00-21:30, Sa-Su 06:00-20:30.
/// </summary>
public class ArrivalsTests
{
    private const long Costco = 1;
    private const long Ultramar = 2;
    private const long Shell = 3;
    private static readonly Car Mazda3 = new(40, 8.5m);
    private static readonly OpeningHours CostcoHours = OpeningHours.Parse("Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30");
    private static readonly TimeSpan Est = TimeSpan.FromHours(-5);

    /// <summary>Wednesday, November 11 2026, at a time, Laval's.</summary>
    private static DateTimeOffset Wednesday(int hour, int minute) => new(2026, 11, 11, hour, minute, 0, Est);

    private static ArrivalHours Trusted(OpeningHours hours, bool temporarilyClosed = false) =>
        new(hours, QuebecTime.Eastern, Trusted: true, temporarilyClosed);

    /// <summary>The stations, all arriving at once: what's kept and what's left out (<see cref="Split"/>).</summary>
    private static string LeaveOut(
        IReadOnlyDictionary<long, ArrivalHours> hours, DateTimeOffset arriveAt, long[] stations, params long[] ignored)
    {
        var (kept, leftOut) = Arrivals.LeaveOutClosed(stations, id => hours.GetValueOrDefault(id), _ => arriveAt, ignored.ToHashSet(), id => id);
        return Split([.. kept], [.. leftOut]);
    }

    /// <summary>What was kept and what was left out, as text that compares by value.</summary>
    private static string Split(long[] kept, long[] leftOut) => $"kept {string.Join(",", kept)}; left out {string.Join(",", leftOut)}";

    [Fact]
    public void Costco_Laval_is_kept_closing_soon_at_21_20_and_left_out_at_21_35()
    {
        var hours = new Dictionary<long, ArrivalHours> { [Costco] = Trusted(CostcoHours) };

        var soon = Arrivals.Check(CostcoHours, QuebecTime.Eastern, Wednesday(21, 20));
        Assert.Equal(Arrival.ClosesSoon, soon.State);
        Assert.Equal(Wednesday(21, 30), soon.ClosesAt);
        Assert.Equal(Split([Costco, Ultramar], []), LeaveOut(hours, Wednesday(21, 20), [Costco, Ultramar]));

        Assert.Equal(Arrival.Closed, Arrivals.Check(CostcoHours, QuebecTime.Eastern, Wednesday(21, 35)).State);
        Assert.Equal(Split([Ultramar], [Costco]), LeaveOut(hours, Wednesday(21, 35), [Costco, Ultramar]));
    }

    [Fact]
    public void Closing_soon_is_half_an_hour_after_the_arrival()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Arrivals.ClosesSoonWithin);
        Assert.Equal(Arrival.ClosesSoon, Arrivals.Check(CostcoHours, QuebecTime.Eastern, Wednesday(21, 0)).State);
        Assert.Equal(Arrival.Open, Arrivals.Check(CostcoHours, QuebecTime.Eastern, Wednesday(20, 59)).State);
    }

    [Fact]
    public void Hours_not_trusted_never_leave_a_station_out()
    {
        // OpenStreetMap's hours, and Costco's from a source gone stale: shown, never trusted to leave it out.
        var osm = new ArrivalHours(CostcoHours, QuebecTime.Eastern, Trusted: false, TemporarilyClosed: false);
        var now = Wednesday(21, 35);
        var stale = HoursPrecedence.Choose(
            [new HoursRow(Costco, HoursPrecedence.Costco, CostcoHours.ToRule(), true, false, null, null)],
            new Dictionary<string, DateTimeOffset> { [HoursPrecedence.Costco] = now.AddDays(-22) },
            now)[Costco];
        Assert.False(stale.Trusted);

        Assert.Equal(Split([Costco, Ultramar], []), LeaveOut(new Dictionary<long, ArrivalHours> { [Costco] = osm }, now, [Costco, Ultramar]));
        Assert.Equal(
            Split([Costco, Ultramar], []),
            LeaveOut(new Dictionary<long, ArrivalHours> { [Costco] = osm with { Trusted = stale.Trusted } }, now, [Costco, Ultramar]));
    }

    [Fact]
    public void Unknown_hours_are_kept()
    {
        Assert.Equal(Split([Ultramar, Shell], []), LeaveOut(new Dictionary<long, ArrivalHours>(), Wednesday(3, 0), [Ultramar, Shell]));
    }

    [Fact]
    public void A_holiday_whose_hours_may_differ_keeps_the_station()
    {
        // Christmas, a Friday: the rule says closed at 22:00, but it doesn't say what a holiday's hours are.
        var christmas = new DateTimeOffset(2026, 12, 25, 22, 0, 0, Est);
        var check = Arrivals.Check(CostcoHours, QuebecTime.Eastern, christmas);
        Assert.Equal((Arrival.Closed, true), (check.State, check.HolidayCaution));

        Assert.Equal(Split([Costco, Ultramar], []), LeaveOut(new Dictionary<long, ArrivalHours> { [Costco] = Trusted(CostcoHours) }, christmas, [Costco, Ultramar]));

        // A rule that states the holidays' hours is sure of them.
        var statesHolidays = Trusted(OpeningHours.Parse("Mo-Su 06:00-23:00; PH off"));
        Assert.Equal(Split([Ultramar], [Costco]), LeaveOut(new Dictionary<long, ArrivalHours> { [Costco] = statesHolidays }, christmas, [Costco, Ultramar]));
    }

    [Fact]
    public void A_station_whose_hours_the_user_corrected_is_kept()
    {
        var hours = new Dictionary<long, ArrivalHours> { [Costco] = Trusted(CostcoHours) };

        Assert.Equal(Split([Costco, Ultramar], []), LeaveOut(hours, Wednesday(21, 35), [Costco, Ultramar], ignored: Costco));
    }

    [Fact]
    public void Temporarily_closed_is_left_out_whatever_its_hours()
    {
        var hours = new Dictionary<long, ArrivalHours> { [Shell] = Trusted(OpeningHours.AlwaysOpen, temporarilyClosed: true) };

        Assert.Equal(Split([Ultramar], [Shell]), LeaveOut(hours, Wednesday(12, 0), [Ultramar, Shell]));
    }

    [Fact]
    public void When_every_station_would_be_left_out_all_are_kept()
    {
        var hours = new Dictionary<long, ArrivalHours>
        {
            [Costco] = Trusted(CostcoHours),
            [Ultramar] = Trusted(OpeningHours.Parse("Mo-Su 06:00-22:00")),
        };

        Assert.Equal(Split([Costco, Ultramar], []), LeaveOut(hours, Wednesday(23, 0), [Costco, Ultramar]));
        Assert.Equal(Split([], []), LeaveOut(hours, Wednesday(23, 0), []));
    }

    [Fact]
    public void Each_station_is_checked_at_its_own_arrival()
    {
        var hours = new Dictionary<long, ArrivalHours> { [Costco] = Trusted(CostcoHours), [Shell] = Trusted(CostcoHours) };
        var minutes = new Dictionary<long, double> { [Costco] = 5, [Shell] = 20, [Ultramar] = 1 };

        var (kept, leftOut) = Arrivals.LeaveOutClosed(
            [Costco, Shell, Ultramar], id => hours.GetValueOrDefault(id), id => Wednesday(21, 20).AddMinutes(minutes[id]), new HashSet<long>(), id => id);

        Assert.Equal([Costco, Ultramar], kept);
        Assert.Equal([Shell], leftOut);
    }

    [Fact]
    public void Near_you_the_next_closest_becomes_the_reference_when_the_closest_is_left_out()
    {
        // Costco is the closest at 0.8 km, closed by 21:35; the Ultramar at 1.5 km and the Shell at 4 km are open.
        Candidate[] candidates = [ByRoad(Costco, 155.9m, 0.8), ByRoad(Ultramar, 166.9m, 1.5), ByRoad(Shell, 163.9m, 4)];
        var hours = new Dictionary<long, ArrivalHours> { [Costco] = Trusted(CostcoHours) };

        var (kept, leftOut) = Arrivals.LeaveOutClosed(
            candidates, c => hours.GetValueOrDefault(c.Id), c => Wednesday(21, 30).AddMinutes(c.DurationMinutes!.Value), new HashSet<long>(), c => c.Id);
        var ranking = NearYou.Rank(kept, Mazda3, maxDetourMinutes: 10, byRoad: true)!;

        Assert.Equal([Costco], leftOut.Select(c => c.Id));
        Assert.Equal(Ultramar, ranking.Reference.Id);
        Assert.True(ranking.Reference.Pickable);
        Assert.Equal(0m, ranking.Reference.Saves);
        Assert.Equal(Shell, ranking.Pick.Id);
        Assert.DoesNotContain(ranking.Ranked, r => r.Id == Costco);
    }

    [Fact]
    public void On_the_way_the_first_station_reached_is_one_that_s_open()
    {
        // Low on fuel (the range less the spare reaches none): the nearest by road is reachable, here Costco at km 20, closed
        // by the time the car gets there; the Ultramar at km 40 becomes the one reached.
        var roads = new TripRoads(
            200,
            120,
            [new TripCandidate(Costco, 154.9m, 20, 20, 12, 180, 108), new TripCandidate(Ultramar, 164.9m, 40, 40, 24, 160, 96)]);
        var hours = new Dictionary<long, ArrivalHours> { [Costco] = Trusted(CostcoHours) };

        var (kept, leftOut) = Arrivals.LeaveOutClosed(
            roads.Candidates, c => hours.GetValueOrDefault(c.Id), c => Wednesday(21, 25).AddMinutes(c.ToMinutes), new HashSet<long>(), c => c.Id);
        var ranking = OnTheWay.Rank(roads with { Candidates = kept }, 200, Mazda3, 10, new FuelRange(35, 30));

        Assert.Equal([Costco], leftOut.Select(c => c.Id));
        Assert.True(ranking.LowFuel);
        Assert.Equal(Ultramar, ranking.Reference!.Id);
        Assert.Equal(Ultramar, ranking.Pick!.Id);
        Assert.True(Assert.Single(ranking.Ranked).Reachable);
    }

    [Fact]
    public void Near_you_the_open_stations_past_ten_closed_ones_are_measured_by_road()
    {
        // At 11:30 p.m., the 10 nearest (0.5 to 5 km, 165.9) close at 11 p.m.; an 11th at 6 km, open 24/7, sells at 167.9.
        // Without counting the closed ones out, only the 10 would be measured, all closed, and one of them picked.
        var closesAt23 = Trusted(OpeningHours.Parse("Mo-Su 06:00-23:00"));
        var stations = Enumerable.Range(1, 11)
            .Select(i => (Id: (long)i, Km: i <= 10 ? i * 0.5 : 6.0, Price: i <= 10 ? 165.9m : 167.9m))
            .ToList();
        var hours = Enumerable.Range(1, 10).ToDictionary(i => (long)i, _ => closesAt23);
        hours[11] = Trusted(OpeningHours.Parse("24/7"));
        var now = Wednesday(23, 30);
        bool LikelyClosed((long Id, double Km, decimal Price) s) =>
            Arrivals.IsClosed(hours.GetValueOrDefault(s.Id), now.AddMinutes(NearYou.Estimate(s.Id, s.Price, s.Km).ExtraMinutes / 2));

        Assert.DoesNotContain(NearYou.SelectByRoad(stations, s => s.Km, s => s.Price), s => s.Id == 11);
        var measured = NearYou.SelectByRoad(stations, s => s.Km, s => s.Price, LikelyClosed);

        var candidates = measured.Select(s => ByRoad(s.Id, s.Price, s.Km)).ToList();
        var (kept, leftOut) = Arrivals.LeaveOutClosed(
            candidates, c => hours.GetValueOrDefault(c.Id), c => now.AddMinutes(c.DurationMinutes!.Value), new HashSet<long>(), c => c.Id);
        var ranking = NearYou.Rank(kept, Mazda3, 10, byRoad: true)!;

        // The 11th is now the nearest open one; the 10 closed, cheaper, are measured too, to be named among those left out.
        Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), measured.Select(s => s.Id));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), leftOut.Select(c => c.Id));
        Assert.Equal(11, ranking.Reference.Id);
        Assert.Equal(11, ranking.Pick.Id);
    }

    [Fact]
    public void Near_you_the_closed_stations_cheaper_than_the_open_nearest_are_still_measured_to_be_named()
    {
        // The nearest (Costco, 154.9) is closed: the 10 open ones after it are the nearest; Costco, cheaper, is measured too.
        var stations = Enumerable.Range(0, 12).Select(i => (Id: (long)i, Km: i * 0.5, Price: i == 0 ? 154.9m : 165.9m)).ToList();

        var measured = NearYou.SelectByRoad(stations, s => s.Km, s => s.Price, s => s.Id == 0);

        Assert.Equal(Enumerable.Range(0, 11).Select(i => (long)i), measured.Select(s => s.Id));
    }

    [Fact]
    public void Near_you_when_every_station_is_likely_closed_the_nearest_are_measured_anyway()
    {
        var stations = Enumerable.Range(0, 12).Select(i => (Id: (long)i, Km: i * 0.5, Price: 165.9m)).ToList();

        var measured = NearYou.SelectByRoad(stations, s => s.Km, s => s.Price, _ => true);

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), measured.Select(s => s.Id));
    }

    [Fact]
    public void On_the_way_a_stretch_s_closest_are_open_ones_first()
    {
        // One stretch: 10 stations by the route, of which the 2 closest are closed, and 190 cheaper ones 2 km off it, which
        // fill the 100 measured. The open ones after the closed are the stretch's closest instead.
        var stations = Enumerable.Range(0, 200)
            .Select(i => i < 10
                ? new RouteStation(i, 165.9m, KmAlong: 1 + i * 0.01, OffKm: 0.1 + i * 0.01)
                : new RouteStation(i, 150m, KmAlong: 1 + i * 0.01, OffKm: 2))
            .ToList();

        var plain = OnTheWay.SelectCandidates(stations, null);
        var candidates = OnTheWay.SelectCandidates(stations, null, s => s.Id is 0 or 1);

        Assert.Equal([0L, 1L], plain.Where(c => c.Id < 10).Select(c => c.Id));
        Assert.Equal([2L, 3L], candidates.Where(c => c.Id < 10).Select(c => c.Id));
        Assert.Equal(OnTheWay.MaxCandidates, candidates.Count);
    }

    /// <summary>A station <paramref name="km"/> away by road, driven there and back at 40 km/h.</summary>
    private static Candidate ByRoad(long id, decimal price, double km) => new(id, price, km, 2 * km, 2 * km / 40 * 60, km / 40 * 60);
}
