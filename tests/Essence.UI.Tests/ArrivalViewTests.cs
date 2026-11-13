using System.Globalization;
using Essence.Contracts;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Essence.UI.Tests;

/// <summary>
/// What Best near you, Best on the way and the Go links say of a station's hours when the driver gets there, on Costco
/// Laval's gas bar (Mo-Fr 06:00-21:30) on Wednesday, November 11 2026, in the station's zone whatever the machine's.
/// </summary>
public sealed class ArrivalViewTests : IDisposable
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";
    private const double Latitude = 45.5699;
    private const double Longitude = -73.7469;
    private static readonly TimeSpan Est = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 11, 0, 0, 0, Est);

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly StationHoursView _view = new(new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)));

    public ArrivalViewTests() => Culture("en-CA");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Theory]
    [InlineData("en-CA", "Closes 9:30 p.m.", "Closes at 9:30 p.m., about 10 min after you get there.", "Costco closes at 9:30 p.m.",
        "You’d get there around 9:20 p.m.")]
    [InlineData("fr-CA", "Ferme à 21 h 30", "Ferme à 21 h 30, environ 10 min après votre arrivée.", "Costco ferme à 21 h 30",
        "Vous y seriez vers 21 h 20.")]
    public void Closing_within_half_an_hour_of_the_arrival_is_said_and_warned(string culture, string closes, string why, string title, string arrive)
    {
        Culture(culture);

        // 9:12 p.m., 8 minutes away: there at 9:20, 10 minutes before it closes.
        var arrival = Arrive(Costco(), At(21, 12), etaMinutes: 8);

        Assert.Equal(ArrivalKind.ClosesSoon, arrival.Kind);
        Assert.Equal(At(21, 20), arrival.ArriveAt);
        Assert.Equal(At(21, 30), arrival.ClosesAt);
        Assert.Equal((closes, why, closes), (Plain(arrival.Suffix), Plain(arrival.Why), Plain(arrival.Tag)));
        Assert.Null(arrival.Blocked);
        Assert.True(arrival.Warns);
        Assert.Equal(title, Plain(arrival.DialogTitle));
        Assert.Equal([arrive], arrival.DialogLines.Select(Plain));
        Assert.Equal($"Shown. Saves $3.10. {why}", Plain(arrival.Announcement("Shown.", ("Saves $3.10", "is-saves", null))));
    }

    [Theory]
    [InlineData("en-CA", "Closed by the time you get there (closes 9:30 p.m.).", "Closed", "Costco closes at 9:30 p.m.", "You’d get there around 9:35 p.m.")]
    [InlineData("fr-CA", "Fermé à votre arrivée (ferme à 21 h 30).", "Fermé", "Costco ferme à 21 h 30", "Vous y seriez vers 21 h 35.")]
    public void Closing_on_the_way_is_closed_on_arrival(string culture, string blocked, string tag, string title, string arrive)
    {
        Culture(culture);

        var arrival = Arrive(Costco(), At(21, 25), etaMinutes: 10);

        Assert.Equal(ArrivalKind.ClosesBefore, arrival.Kind);
        Assert.Equal(At(21, 30), arrival.ClosesAt);
        Assert.Equal(At(6, 0).AddDays(1), arrival.OpensAt);
        Assert.Equal((blocked, tag), (Plain(arrival.Blocked), Plain(arrival.Tag)));
        Assert.Null(arrival.Suffix);
        Assert.Null(arrival.Why);
        Assert.True(arrival.Warns);
        Assert.Equal(title, Plain(arrival.DialogTitle));
        Assert.Equal([arrive], arrival.DialogLines.Select(Plain));
    }

    [Theory]
    [InlineData("en-CA", "Closed when you get there (opens 6:00 a.m.).", "Costco is closed", "You’d get there around 10:05 p.m.", "Opens 6:00 a.m.")]
    [InlineData("fr-CA", "Fermé à votre arrivée (ouvre à 6 h).", "Costco est fermé", "Vous y seriez vers 22 h 05.", "Ouvre à 6 h.")]
    public void Closed_already_says_when_it_opens(string culture, string blocked, string title, string arrive, string opens)
    {
        Culture(culture);

        var arrival = Arrive(Costco(), At(22, 0), etaMinutes: 5);

        Assert.Equal(ArrivalKind.Closed, arrival.Kind);
        Assert.Equal(blocked, Plain(arrival.Blocked));
        Assert.Equal(title, Plain(arrival.DialogTitle));
        Assert.Equal([arrive, opens], arrival.DialogLines.Select(Plain));
    }

    [Theory]
    [InlineData("en-CA", "Closed when you get there (opens Mon 7:00 a.m.).")]
    [InlineData("fr-CA", "Fermé à votre arrivée (ouvre lun. à 7 h).")]
    public void Closed_until_after_tomorrow_names_the_day_it_opens(string culture, string blocked)
    {
        Culture(culture);

        // Saturday evening, at a station closed on Sundays.
        var arrival = Arrive(Hours("Mo-Sa 07:00-19:00"), At(20, 0).AddDays(3), etaMinutes: 5);

        Assert.Equal(blocked, Plain(arrival.Blocked));
    }

    [Fact]
    public void Temporarily_closed_is_closed_whatever_the_hours()
    {
        var arrival = Arrive(Costco() with { TemporarilyClosed = true }, At(12, 0), etaMinutes: 5);

        Assert.Equal(ArrivalKind.TemporarilyClosed, arrival.Kind);
        Assert.Equal(("Temporarily closed", "Temporarily closed"), (arrival.Blocked, arrival.Tag));
        Assert.Equal("Costco is closed", arrival.DialogTitle);
        Assert.Equal(["Its source says it’s closed for now."], arrival.DialogLines);
    }

    [Theory]
    [InlineData(15, 0, "Mo-Fr 06:00-21:30")]
    [InlineData(23, 50, "24/7")]
    [InlineData(23, 50, null)]
    [InlineData(23, 50, "sunrise-sunset")]
    public void Open_well_after_the_arrival_or_hours_unknown_go_straight_to_Google_Maps(int hour, int minute, string? rule)
    {
        var arrival = Arrive(rule is null ? null : Hours(rule), At(hour, minute), etaMinutes: 10);

        Assert.False(arrival.Warns);
        Assert.Null(arrival.DialogTitle);
        Assert.Empty(arrival.DialogLines);
        Assert.Null(arrival.Tag);
        Assert.Equal("Shown. Saves $3.10.", arrival.Announcement("Shown.", ("Saves $3.10", "is-saves", "Why.")));
        Assert.Equal(rule is null or "sunrise-sunset" ? ArrivalKind.Unknown : ArrivalKind.Open, arrival.Kind);
    }

    [Fact]
    public void Closed_on_arrival_is_announced_instead_of_the_saving()
    {
        var arrival = Arrive(Costco(), At(21, 25), etaMinutes: 10);

        Assert.Equal(
            "Shown above: Costco. Closed by the time you get there (closes 9:30 p.m.).",
            Plain(arrival.Announcement("Shown above: Costco.", ("Saves $3.10", "is-saves", "Cheapest nearby."))));
    }

    [Theory]
    // Open on arrival (8:55): it starts closing soon when the arrival reaches 9:00, at 8:45.
    [InlineData(20, 40, 0, 15, 20, 45, 0)]
    // Closing soon: in a minute, "about N min" changes.
    [InlineData(21, 0, 0, 15, 21, 1, 0)]
    // Closing soon, 30 s before it'd close on arrival: then, not a minute later.
    [InlineData(21, 14, 30, 15, 21, 15, 0)]
    // Closes on the way: at 9:30, when it's closed now.
    [InlineData(21, 20, 0, 15, 21, 30, 0)]
    // Closed: when the arrival reaches 6:00 the next morning, at 5:45.
    [InlineData(22, 0, 0, 15, 29, 45, 0)]
    public void An_arrival_says_when_what_it_says_changes(
        int hour, int minute, int second, double eta, int refreshHour, int refreshMinute, int refreshSecond)
    {
        var now = At(hour, minute).AddSeconds(second);

        var arrival = Arrive(Costco(), now, etaMinutes: eta);

        Assert.Equal(At(refreshHour, refreshMinute).AddSeconds(refreshSecond), arrival.RefreshAt(now));
    }

    [Fact]
    public void An_arrival_that_can_t_change_needs_no_refresh()
    {
        Assert.Null(Arrive(Hours("24/7"), At(12, 0), etaMinutes: 10).RefreshAt(At(12, 0)));
        Assert.Null(Arrive(null, At(12, 0), etaMinutes: 10).RefreshAt(At(12, 0)));
        Assert.Null(Arrive(Costco() with { TemporarilyClosed = true }, At(12, 0), etaMinutes: 10).RefreshAt(At(12, 0)));
    }

    [Fact]
    public void Without_a_time_to_get_there_the_hours_are_checked_now_and_the_warning_doesn_t_guess_the_arrival()
    {
        var arrival = Arrive(Costco(), At(21, 12), etaMinutes: null);

        Assert.Equal((ArrivalKind.ClosesSoon, At(21, 12)), (arrival.Kind, arrival.ArriveAt));
        Assert.True(arrival.Warns);
        Assert.Empty(arrival.DialogLines);
        Assert.Equal("Closes at 9:30 p.m., about 18 min after you get there.", Plain(arrival.Why));
    }

    [Fact]
    public void The_islands_close_on_their_own_clock()
    {
        // 9:15 p.m. in Cap-aux-Meules, an hour ahead of Laval.
        var arrival = _view.Arrive(Hours("Mo-Su 06:00-21:30"), 47.38, -61.86, At(20, 15), 5, "Irving");

        Assert.Equal(ArrivalKind.ClosesSoon, arrival.Kind);
        Assert.Equal("Irving closes at 9:30 p.m.", Plain(arrival.DialogTitle));
        Assert.Equal(["You’d get there around 9:20 p.m."], arrival.DialogLines.Select(Plain));
    }

    [Theory]
    [InlineData("en-CA", "Closes at 9:30 p.m., about 10 min after you get there. Counted against the Ultramar.")]
    [InlineData("fr-CA", "Ferme à 21 h 30, environ 10 min après votre arrivée. Counted against the Ultramar.")]
    public void The_hero_s_explanation_starts_with_the_closing_time(string culture, string why)
    {
        Culture(culture);
        var arrival = Arrive(Costco(), At(21, 12), etaMinutes: 8);

        var hero = arrival.Apply(("Saves $3.10", null, "Counted against the Ultramar."));

        Assert.Equal(("Saves $3.10", (string?)null, why), (hero.Saves, hero.Tone, Plain(hero.Why)));
        Assert.Equal(Plain(arrival.Why), Plain(arrival.Apply(("Saves $3.10", null, null)).Why));
    }

    [Theory]
    [InlineData("en-CA", "Closed by the time you get there (closes 9:30 p.m.).")]
    [InlineData("fr-CA", "Fermé à votre arrivée (ferme à 21 h 30).")]
    public void Closed_on_arrival_the_hero_says_so_in_amber_instead_of_the_saving(string culture, string blocked)
    {
        Culture(culture);
        var arrival = Arrive(Costco(), At(21, 25), etaMinutes: 10);

        var hero = arrival.Apply(("Saves $3.10", null, "Counted against the Ultramar."));

        Assert.Equal((blocked, "is-blocked", "Counted against the Ultramar."), (Plain(hero.Saves), hero.Tone, hero.Why));
    }

    [Fact]
    public void Open_on_arrival_leaves_the_hero_as_it_is()
    {
        (string?, string?, string?) hero = ("About the same", "is-neutral", "Counted against the Ultramar.");

        Assert.Equal(hero, Arrive(Costco(), At(15, 0), etaMinutes: 8).Apply(hero));
        Assert.Equal(hero, Arrive(null, At(23, 0), etaMinutes: 8).Apply(hero));
    }

    [Theory]
    [InlineData("en-CA", 21, 25, "Costco left out: closed by the time you’d get there (closes 9:30 p.m.).")]
    [InlineData("fr-CA", 21, 25, "Costco écarté : fermé à votre arrivée (ferme à 21 h 30).")]
    [InlineData("en-CA", 22, 0, "Costco left out: closed when you’d get there (opens 6:00 a.m.).")]
    [InlineData("fr-CA", 22, 0, "Costco écarté : fermé à votre arrivée (ouvre à 6 h).")]
    public void A_station_left_out_says_why(string culture, int hour, int minute, string text)
    {
        Culture(culture);

        Assert.Equal(text, Plain(_view.LeftOut(LeftOut(Costco(), etaMinutes: 10), "Costco", At(hour, minute))));
    }

    [Theory]
    [InlineData("en-CA", "Shell left out: temporarily closed.")]
    [InlineData("fr-CA", "Shell écarté : fermé temporairement.")]
    public void A_station_closed_for_now_says_that(string culture, string text)
    {
        Culture(culture);

        Assert.Equal(text, _view.LeftOut(LeftOut(Costco() with { TemporarilyClosed = true }, etaMinutes: 3), "Shell", At(12, 0)));
    }

    [Fact]
    public void A_station_left_out_that_s_open_by_now_says_nothing()
    {
        // Left out at 5:50 a.m., 5 minutes away; at 5:58, it would be open on arrival.
        Assert.NotNull(_view.LeftOut(LeftOut(Costco(), etaMinutes: 5), "Costco", At(5, 50)));
        Assert.Null(_view.LeftOut(LeftOut(Costco(), etaMinutes: 5), "Costco", At(5, 58)));
    }

    [Fact]
    public void The_details_estimate_the_drive_as_the_API_does_without_routing() =>
        Assert.Equal(19.5, StationHoursView.EstimatedMinutes(10), 6);

    private ArrivalView Arrive(StationHours? hours, DateTimeOffset now, double? etaMinutes) =>
        _view.Arrive(hours, Latitude, Longitude, now, etaMinutes, "Costco");

    private static LeftOutStation LeftOut(StationHours hours, double etaMinutes) =>
        new(1, "Costco Laval", "Costco", "2999 Autoroute Chomedey", Latitude, Longitude, 153.9m, hours, etaMinutes);

    private static DateTimeOffset At(int hour, int minute) => Wednesday.AddHours(hour).AddMinutes(minute);

    private static StationHours Costco() => Hours(CostcoRule) with { Source = "costco" };

    private static StationHours Hours(string rule) => new(rule, "curated", Trusted: true, Confirmed: true, CheckedAt: null, Link: null);

    private static void Culture(string name) =>
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    /// <summary>Plain spaces: before "a.m.", ICU writes a narrow no-break space or a plain one, depending on its version.</summary>
    private static string? Plain(string? text) =>
        text is null ? null : string.Concat(text.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c));
}
