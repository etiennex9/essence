using System.Globalization;
using Essence.Contracts;
using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Essence.UI.Tests;

/// <summary>
/// A station's hours as the app says them, every time in the station's zone: Laval's Eastern time, or the
/// Îles-de-la-Madeleine's Atlantic time. The islands' cases prove it on any machine, Laval at the same moment saying
/// another time; Laval's prove it too where the machine's zone isn't Eastern, as on the CI's runners (UTC).
/// </summary>
public sealed class StationHoursViewTests : IDisposable
{
    private const string CostcoRule = "Mo-Fr 06:00-21:30; Sa-Su 06:00-20:30";

    /// <summary>Costco Laval's gas bar.</summary>
    private const double Latitude = 45.5699;
    private const double Longitude = -73.7469;

    /// <summary>Cap-aux-Meules, on the Îles-de-la-Madeleine.</summary>
    private static readonly (double Latitude, double Longitude) Islands = (47.38, -61.86);

    private static readonly TimeSpan Est = TimeSpan.FromHours(-5);

    /// <summary>Wednesday, November 11 2026.</summary>
    private static readonly DateTimeOffset Wednesday = new(2026, 11, 11, 0, 0, 0, Est);

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly StationHoursView _view = new(new StringLocalizer<Strings>(new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)));

    public StationHoursViewTests() => Culture("en-CA");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Theory]
    [InlineData("en-CA", "Open until 9:30 p.m.", "Closes 9:30 p.m.")]
    [InlineData("fr-CA", "Ouvert jusqu’à 21 h 30", "Ferme à 21 h 30")]
    public void Closing_within_half_an_hour_is_closing_soon(string culture, string status, string tag)
    {
        Culture(culture);

        var view = Describe(Costco(), At(21, 15));

        Assert.Equal(HoursKind.ClosesSoon, view.Kind);
        Assert.Equal((status, tag), (Plain(view.Status), Plain(view.Tag)));
        Assert.Equal(At(21, 30), view.ChangesAt);
        Assert.Equal(At(21, 30), view.RefreshAt);
        Assert.False(view.IsClosed);
    }

    [Fact]
    public void Open_for_longer_says_until_when_and_shows_again_as_it_starts_closing_soon()
    {
        var view = Describe(Costco(), At(15, 0));

        Assert.Equal(HoursKind.Open, view.Kind);
        Assert.Equal("Open until 9:30 p.m.", Plain(view.Status));
        Assert.Equal("Closes 9:30 p.m.", Plain(view.Closes));
        Assert.Null(view.Tag);
        Assert.Equal(At(21, 0), view.RefreshAt);
    }

    [Theory]
    [InlineData("en-CA", "Closed · opens 6:00 a.m.", "Closed")]
    [InlineData("fr-CA", "Fermé · ouvre à 6 h", "Fermé")]
    public void Closed_says_when_it_opens(string culture, string status, string tag)
    {
        Culture(culture);

        var view = Describe(Costco(), At(21, 45));

        Assert.Equal(HoursKind.Closed, view.Kind);
        Assert.Equal((status, tag), (Plain(view.Status), Plain(view.Tag)));
        Assert.Equal(At(6, 0).AddDays(1), view.ChangesAt);
        Assert.True(view.IsClosed);
    }

    [Theory]
    [InlineData("en-CA", "Closed · opens Mon 7:00 a.m.")]
    [InlineData("fr-CA", "Fermé · ouvre lun. à 7 h")]
    public void Closed_until_after_tomorrow_says_the_day_it_opens(string culture, string status)
    {
        Culture(culture);

        // Saturday evening, at a station closed on Sundays.
        var view = Describe(Hours("Mo-Sa 07:00-19:00"), At(20, 0).AddDays(3));

        Assert.Equal(status, Plain(view.Status));
        Assert.Equal(At(7, 0).AddDays(5), view.ChangesAt);
    }

    [Theory]
    [InlineData("en-CA", "Open until Fri midnight", "Closed · opens Sun midnight")]
    [InlineData("fr-CA", "Ouvert jusqu’au ven. à minuit", "Fermé · ouvre dim. à minuit")]
    public void Midnight_days_away_is_the_end_of_the_day_it_names(string culture, string open, string closed)
    {
        Culture(culture);
        var weekdays = Hours("Mo-Fr 00:00-24:00; Sa-Su 07:00-23:00");

        // Open from Monday's start to Friday's end; closed Saturday noon until Sunday's end (Monday's start).
        var wednesday = Describe(weekdays, At(12, 0));
        var saturday = Describe(Hours("Mo-Fr 00:00-24:00"), At(12, 0).AddDays(3));

        Assert.Equal(open, Plain(wednesday.Status));
        Assert.Equal(At(0, 0).AddDays(3), wednesday.ChangesAt);
        Assert.Equal(closed, Plain(saturday.Status));
        Assert.Equal(At(0, 0).AddDays(5), saturday.ChangesAt);
    }

    [Fact]
    public void The_times_are_the_station_s_own_on_the_islands()
    {
        // 9:15 p.m. in Cap-aux-Meules, an hour ahead of Laval.
        var now = At(20, 15);

        var islands = _view.Describe(Hours("Mo-Su 06:00-21:30"), Islands.Latitude, Islands.Longitude, now);
        var laval = Describe(Hours("Mo-Su 06:00-21:30"), now);

        Assert.Equal((HoursKind.ClosesSoon, "Open until 9:30 p.m."), (islands.Kind, Plain(islands.Status)));
        Assert.Equal(At(20, 30), islands.ChangesAt);
        Assert.Equal((HoursKind.Open, (DateTimeOffset?)At(21, 30)), (laval.Kind, laval.ChangesAt));
    }

    [Theory]
    [InlineData("en-CA", "Closed · opens 6:00 a.m.")]
    [InlineData("fr-CA", "Fermé · ouvre à 6 h")]
    public void The_islands_open_on_their_own_clock_too(string culture, string status)
    {
        Culture(culture);

        // 5:30 a.m. in Cap-aux-Meules: it opens at 6:00 there, 5:00 a.m. in Laval.
        var islands = _view.Describe(Hours("Mo-Su 06:00-21:30"), Islands.Latitude, Islands.Longitude, At(4, 30));

        Assert.Equal((HoursKind.Closed, status), (islands.Kind, Plain(islands.Status)));
        Assert.Equal(At(5, 0), islands.ChangesAt);
    }

    [Fact]
    public void Open_all_the_time_never_changes()
    {
        var view = Describe(Hours("24/7"), At(3, 0));

        Assert.Equal((HoursKind.AlwaysOpen, "Open 24 hours"), (view.Kind, view.Status));
        Assert.Null(view.ChangesAt);
        Assert.Null(view.RefreshAt);
        Assert.Null(view.Tag);
        Assert.Equal([new HoursLine("Mon–Sun", "Open 24 hours", true)], view.Week);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sunrise-sunset")]
    [InlineData("")]
    public void Without_hours_that_can_be_read_they_re_unknown(string? rule)
    {
        var view = Describe(rule is null ? null : Hours(rule), At(12, 0));

        Assert.Same(HoursView.Unknown, view);
        Assert.Equal(HoursKind.Unknown, view.Kind);
        Assert.Null(view.Status);
        Assert.Empty(view.Week);
    }

    [Fact]
    public void A_holiday_whose_hours_the_rule_doesnt_say_is_a_caution()
    {
        var christmas = new DateTimeOffset(2026, 12, 25, 10, 0, 0, Est);

        var dayBefore = Describe(Costco(), christmas.AddDays(-1));
        var view = Describe(Costco(), christmas);

        Assert.Equal(HoursKind.Open, view.Kind);
        Assert.True(view.HolidayCaution);
        Assert.False(dayBefore.HolidayCaution);
    }

    [Fact]
    public void A_holiday_the_rule_gives_hours_for_has_its_own_line_and_no_caution()
    {
        var christmas = new DateTimeOffset(2026, 12, 25, 10, 0, 0, Est);

        var view = Describe(Hours("Mo-Su 06:00-22:00; PH off"), christmas);

        Assert.Equal(HoursKind.Closed, view.Kind);
        Assert.False(view.HolidayCaution);
        Assert.Equal(
            [new HoursLine("Mon–Sun", "6:00 a.m. – 10:00 p.m.", false), new HoursLine("Holidays", "Closed", true)],
            view.Week.Select(Plain));
    }

    [Fact]
    public void Temporarily_closed_is_closed_whatever_the_hours()
    {
        var view = Describe(Costco() with { TemporarilyClosed = true }, At(12, 0));

        Assert.Equal(HoursKind.TemporarilyClosed, view.Kind);
        Assert.Equal(("Temporarily closed", "Temporarily closed"), (view.Status, view.Tag));
        Assert.True(view.IsClosed);
        Assert.Null(view.RefreshAt);
        Assert.Equal(2, view.Week.Count);
    }

    [Theory]
    [InlineData("en-CA", "Mon–Fri", "6:00 a.m. – 9:30 p.m.", "Sat–Sun", "6:00 a.m. – 8:30 p.m.")]
    [InlineData("fr-CA", "lun.–ven.", "6 h – 21 h 30", "sam.–dim.", "6 h – 20 h 30")]
    public void The_week_is_in_groups_of_days_Monday_first_with_today_s_flagged(
        string culture, string weekdays, string weekdayHours, string weekend, string weekendHours)
    {
        Culture(culture);

        var wednesday = Describe(Costco(), At(12, 0));
        var sunday = Describe(Costco(), At(12, 0).AddDays(4));

        Assert.Equal([new HoursLine(weekdays, weekdayHours, true), new HoursLine(weekend, weekendHours, false)], wednesday.Week.Select(Plain));
        Assert.Equal([false, true], sunday.Week.Select(l => l.Today));
    }

    [Theory]
    [InlineData("en-CA", "Sun", "Closed", "6:00 a.m. – midnight", "10:00 p.m. – 2:00 a.m.")]
    [InlineData("fr-CA", "dim.", "Fermé", "6 h – minuit", "22 h – 2 h")]
    public void Closed_days_midnight_and_hours_past_it_read_plainly(string culture, string sunday, string closed, string toMidnight, string pastMidnight)
    {
        Culture(culture);

        var week = Describe(Hours("Mo-Th 06:00-24:00; Fr-Sa 22:00-02:00"), At(12, 0)).Week.Select(Plain).ToList();

        Assert.Equal(new HoursLine(sunday, closed, false), week[^1]);
        Assert.Equal(toMidnight, week[0].Hours);
        Assert.Equal(pastMidnight, week[1].Hours);
    }

    [Theory]
    [InlineData("costco", null, "en-CA", "Costco")]
    [InlineData("circlek", null, "en-CA", "Couche-Tard")]
    [InlineData("shell", null, "fr-CA", "Shell")]
    [InlineData("curated", "2026-11-11T00:00:00-05:00", "en-CA", "Checked by Essence, November 11")]
    [InlineData("curated", "2026-11-11T00:00:00-05:00", "fr-CA", "Vérifiées par Essence, 11 novembre")]
    [InlineData("curated", "2025-06-02T00:00:00-04:00", "en-CA", "Checked by Essence, June 2025")]
    [InlineData("curated", null, "en-CA", "Checked by Essence")]
    [InlineData("osm", "2025-03-04T12:00:00Z", "en-CA", "OpenStreetMap, edited 2025")]
    [InlineData("osm", "2025-03-04T12:00:00Z", "fr-CA", "OpenStreetMap, modifiées en 2025")]
    [InlineData("osm", null, "en-CA", "OpenStreetMap")]
    [InlineData("acme", null, "en-CA", "acme")]
    public void The_source_is_named_with_when_it_was_checked(string source, string? checkedAt, string culture, string expected)
    {
        Culture(culture);
        var hours = Costco() with { Source = source, CheckedAt = checkedAt is null ? null : DateTimeOffset.Parse(checkedAt, CultureInfo.InvariantCulture) };

        Assert.Equal(expected, Describe(hours, At(12, 0)).Source);
    }

    [Theory]
    [InlineData("https://www.costco.ca/w/-/qc/laval/1182", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("costco.ca", false)]
    [InlineData(null, false)]
    public void Only_a_web_address_is_linked(string? link, bool linked) =>
        Assert.Equal(linked ? link : null, Describe(Costco() with { Link = link }, At(12, 0)).SourceLink);

    [Fact]
    public void A_marker_is_closed_when_the_hours_say_so_or_the_source_does()
    {
        Assert.Equal((false, (DateTimeOffset?)At(21, 30)), StationHoursView.MarkerAt(Costco(), Latitude, Longitude, At(21, 15)));
        Assert.Equal((true, (DateTimeOffset?)At(6, 0).AddDays(1)), StationHoursView.MarkerAt(Costco(), Latitude, Longitude, At(21, 45)));
        Assert.Equal((true, (DateTimeOffset?)null), StationHoursView.MarkerAt(Costco() with { TemporarilyClosed = true }, Latitude, Longitude, At(12, 0)));
        Assert.Equal((false, (DateTimeOffset?)null), StationHoursView.MarkerAt(Hours("24/7"), Latitude, Longitude, At(21, 45)));
        Assert.Equal((false, (DateTimeOffset?)null), StationHoursView.MarkerAt(null, Latitude, Longitude, At(21, 45)));
        Assert.Equal((false, (DateTimeOffset?)null), StationHoursView.MarkerAt(Hours("sunrise-sunset"), Latitude, Longitude, At(21, 45)));
    }

    /// <summary>Costco Laval's hours at that moment.</summary>
    private HoursView Describe(StationHours? hours, DateTimeOffset at) => _view.Describe(hours, Latitude, Longitude, at);

    /// <summary>That Wednesday at a time, Laval's.</summary>
    private static DateTimeOffset At(int hour, int minute) => Wednesday.AddHours(hour).AddMinutes(minute);

    private static StationHours Costco() => Hours(CostcoRule) with { Source = "costco" };

    private static StationHours Hours(string rule) => new(rule, "curated", Trusted: true, Confirmed: true, CheckedAt: null, Link: null);

    private static void Culture(string name) =>
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    /// <summary>Plain spaces: before "a.m.", ICU writes a narrow no-break space or a plain one, depending on its version.</summary>
    private static string? Plain(string? text) =>
        text is null ? null : string.Concat(text.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c));

    private static HoursLine Plain(HoursLine line) => line with { Days = Plain(line.Days)!, Hours = Plain(line.Hours)! };
}
