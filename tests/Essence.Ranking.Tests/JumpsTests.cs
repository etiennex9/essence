using static Essence.Ranking.Tests.TrendData;

namespace Essence.Ranking.Tests;

public class JumpsTests
{
    private static readonly DateTimeOffset Midnight = Local(11, 20, 0);

    /// <summary>Since the afternoon before: a price set less than 2 days ago, as a station that follows has.</summary>
    private static readonly DateTimeOffset Base = Local(11, 19, 4);

    /// <summary>
    /// A Montréal-like night: 20 stations at 170.0 since the afternoon before; <paramref name="risers"/> of them go up to
    /// 180.0 between midnight and 00:15, at the imports.
    /// </summary>
    private static List<AreaStation> Night(int risers, int stations = 20)
    {
        var area = new List<AreaStation>();
        for (var i = 0; i < stations; i++)
        {
            area.Add(i < risers
                ? Station(i, (Base, 170.0m), (Midnight.AddMinutes(i * 15 / Math.Max(1, risers - 1) / 5 * 5), 180.0m))
                : Station(i, (Base, 170.0m)));
        }

        return area;
    }

    private static readonly Coverage Covered = Imports(Midnight.AddDays(-3), Midnight.AddDays(1));

    private static StationSet All(int stations = 20) => StationSet.Of(Enumerable.Range(0, stations).Select(i => (long)i));

    private static JumpState? At(List<AreaStation> area, DateTimeOffset at, StationSet? followers = null, Coverage? coverage = null) =>
        Jumps.Detect(area, coverage ?? Covered, at.UtcTicks, followers ?? All(area.Count));

    [Fact]
    public void A_night_jump_lists_the_followers_still_at_their_old_price()
    {
        var jump = At(Night(12), Midnight.AddMinutes(20))!;

        Assert.Equal(12, jump.Raised);
        Assert.Equal(20, jump.Stations);
        Assert.Equal(10m, jump.RiseCents);
        Assert.Equal((170m, 180m), (jump.BeforeCents, jump.AfterCents));
        Assert.Equal(Enumerable.Range(12, 8).Select(i => new HeldPrice(i, 170.0m)), jump.NotRaised);

        // 5 of 20 needed: the fifth rise, at 00:05.
        Assert.Equal(Midnight.AddMinutes(5), jump.Since);
        Assert.Equal(Midnight.AddMinutes(15 + 45), jump.Until);
    }

    [Fact]
    public void Stations_that_dont_usually_follow_arent_listed()
    {
        var jump = At(Night(12), Midnight.AddMinutes(20), followers: All(16))!;

        Assert.Equal([12L, 13, 14, 15], jump.NotRaised.Select(h => h.StationId));
    }

    [Fact]
    public void A_station_up_a_little_or_already_up_isnt_listed()
    {
        var area = Night(12);
        area[12] = Station(12, (Base, 170.0m), (Midnight.AddMinutes(10), 173.0m));
        area[13] = Station(13, (Base, 170.0m), (Midnight.AddHours(-2), 181.0m));

        var jump = At(area, Midnight.AddMinutes(20))!;

        Assert.DoesNotContain(jump.NotRaised, h => h.StationId is 12 or 13);
        Assert.Equal(13, jump.Raised);
    }

    [Fact]
    public void Too_few_rises_or_stations_make_no_jump()
    {
        Assert.Null(At(Night(4), Midnight.AddMinutes(20)));
        Assert.Null(At(Night(5, stations: 7), Midnight.AddMinutes(20)));
        Assert.NotNull(At(Night(5), Midnight.AddMinutes(20)));
    }

    [Fact]
    public void The_jump_ends_45_minutes_after_the_last_follower_rise()
    {
        var area = Night(12);

        Assert.NotNull(At(area, Midnight.AddMinutes(59)));
        Assert.Null(At(area, Midnight.AddMinutes(61)));
    }

    [Fact]
    public void A_follower_rising_after_the_end_doesnt_bring_it_back()
    {
        var area = Night(12);
        area[19] = Station(19, (Base, 170.0m), (Midnight.AddMinutes(90), 180.0m));

        Assert.Null(At(area, Midnight.AddMinutes(95)));
    }

    [Fact]
    public void Rises_go_on_for_3_hours_at_most()
    {
        var area = new List<AreaStation>();
        for (var i = 0; i < 20; i++)
        {
            area.Add(Station(i, (Base, 170.0m), (Midnight.AddMinutes(i * 15), 180.0m)));
        }

        // A station a quarter-hour: the fifth rise at 01:00 starts it, and each one keeps it going until 04:00.
        Assert.Equal(Midnight.AddHours(1), At(area, Midnight.AddMinutes(230))!.Since);
        Assert.Null(At(area, Midnight.AddMinutes(245)));
    }

    [Fact]
    public void Rises_at_an_import_after_a_gap_arent_a_jump()
    {
        var gap = Imports(Midnight.AddDays(-3), Midnight.AddDays(1), (Midnight.AddHours(-3), Midnight.AddMinutes(10)));
        var area = new List<AreaStation>();
        for (var i = 0; i < 20; i++)
        {
            area.Add(i < 12
                ? Station(i, (Base, 170.0m), (Midnight.AddMinutes(10), 180.0m))
                : Station(i, (Base, 170.0m)));
        }

        Assert.Null(At(area, Midnight.AddMinutes(20), coverage: gap));
    }

    [Fact]
    public void A_short_gap_during_the_jump_keeps_it()
    {
        var gap = Imports(Midnight.AddDays(-3), Midnight.AddDays(1), (Midnight.AddMinutes(16), Midnight.AddMinutes(40)));

        var jump = At(Night(12), Midnight.AddMinutes(45), coverage: gap);

        Assert.NotNull(jump);
        Assert.Equal(12, jump.Raised);
    }

    [Fact]
    public void An_evening_batch_doesnt_hide_the_nights_jump()
    {
        // 6 of 24 rise at 21:00 (a jump of its own, over by 21:45), then 12 others at midnight.
        var area = new List<AreaStation>();
        for (var i = 0; i < 24; i++)
        {
            area.Add(i switch
            {
                < 6 => Station(i, (Base, 170.0m), (Midnight.AddHours(-3), 180.0m)),
                < 18 => Station(i, (Base, 170.0m), (Midnight.AddMinutes((i - 6) % 3 * 5), 180.0m)),
                _ => Station(i, (Base, 170.0m)),
            });
        }

        var evening = At(area, Midnight.AddHours(-3).AddMinutes(10))!;
        var night = At(area, Midnight.AddMinutes(20))!;

        Assert.Equal(Midnight.AddHours(-3), evening.Since);
        Assert.Equal(Midnight.AddMinutes(5), night.Since);
        Assert.Equal(12, night.Raised);
        Assert.Equal(6, night.NotRaised.Count);
    }

    [Fact]
    public void A_failed_evening_attempt_doesnt_hide_the_nights_jump()
    {
        // 5 go up at 18:00 and back down at 19:00, then 7 go up at midnight, those 5 among them.
        var area = new List<AreaStation>();
        for (var i = 0; i < 20; i++)
        {
            area.Add(i switch
            {
                < 5 => Station(i, (Base, 170.0m), (Midnight.AddHours(-6), 180.0m), (Midnight.AddHours(-5), 170.0m), (Midnight.AddMinutes(5), 180.0m)),
                < 7 => Station(i, (Base, 170.0m), (Midnight.AddMinutes(5), 180.0m)),
                _ => Station(i, (Base, 170.0m)),
            });
        }

        var jump = At(area, Midnight.AddMinutes(20))!;

        Assert.Equal(Midnight.AddMinutes(5), jump.Since);
        Assert.Equal(7, jump.Raised);
        Assert.Equal(13, jump.NotRaised.Count);
    }

    [Fact]
    public void An_early_riser_cutting_back_doesnt_bring_an_ended_jump_back()
    {
        // 3 of 12 at 00:05 (over at 00:50); at 01:10 one of them cuts back and a follower goes up.
        var area = new List<AreaStation>();
        for (var i = 0; i < 12; i++)
        {
            area.Add(i switch
            {
                0 => Station(i, (Base, 170.0m), (Midnight.AddMinutes(5), 180.0m), (Midnight.AddMinutes(70), 170.0m)),
                < 3 => Station(i, (Base, 170.0m), (Midnight.AddMinutes(5), 180.0m)),
                3 => Station(i, (Base, 170.0m), (Midnight.AddMinutes(70), 180.0m)),
                _ => Station(i, (Base, 170.0m)),
            });
        }

        Assert.NotNull(At(area, Midnight.AddMinutes(45)));
        Assert.Null(At(area, Midnight.AddMinutes(75)));
    }

    [Fact]
    public void A_cut_hours_after_a_rise_isnt_a_rise()
    {
        var prices = Prices(
            (Midnight.AddHours(-4), 150.0m), (Midnight.AddMinutes(5), 160.0m), (Midnight.AddMinutes(190), 159.0m), (Midnight.AddHours(5), 165.0m));

        var rises = Rises.Of(prices, Covered, Midnight.AddHours(-1).UtcTicks, Midnight.AddHours(6).UtcTicks, Midnight.AddHours(6).UtcTicks);

        Assert.Equal([Midnight.AddMinutes(5).UtcTicks, Midnight.AddHours(5).UtcTicks], rises.Select(r => r.At));
    }

    [Fact]
    public void A_follower_whose_price_hasnt_changed_in_days_isnt_listed()
    {
        var area = Night(12);
        area[19] = Station(19, (Midnight.AddDays(-5), 170.0m));

        var jump = At(area, Midnight.AddMinutes(20))!;

        Assert.DoesNotContain(jump.NotRaised, h => h.StationId == 19);
        Assert.Equal(7, jump.NotRaised.Count);
    }

    [Fact]
    public void A_rise_in_two_steps_starts_once()
    {
        // 12 of 20 go up 3 ¢ at 00:05, then 3 ¢ more at 00:35: a rise of 6 ¢ at the second step.
        var area = new List<AreaStation>();
        for (var i = 0; i < 20; i++)
        {
            area.Add(i < 12
                ? Station(i, (Base, 170.0m), (Midnight.AddMinutes(5), 173.0m), (Midnight.AddMinutes(35), 176.0m))
                : Station(i, (Base, 170.0m)));
        }

        Assert.Equal(Midnight.AddMinutes(35), At(area, Midnight.AddMinutes(40))!.Since);
        Assert.Equal(Midnight.AddMinutes(35), At(area, Midnight.AddMinutes(70))!.Since);
    }

    [Fact]
    public void Everyone_up_3_cents_isnt_a_jump()
    {
        var area = new List<AreaStation>();
        for (var i = 0; i < 20; i++)
        {
            area.Add(Station(i, (Base, 170.0m), (Midnight.AddMinutes(5), 173.0m)));
        }

        Assert.Null(At(area, Midnight.AddMinutes(20)));
    }

    [Fact]
    public void Without_followers_known_no_station_is_listed()
    {
        var jump = Jumps.Detect(Night(12), Covered, Midnight.AddMinutes(20).UtcTicks, followers: null)!;

        Assert.Empty(jump.NotRaised);
        Assert.Equal(12, jump.Raised);
    }
}
