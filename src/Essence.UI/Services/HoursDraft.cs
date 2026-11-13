using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Essence.Contracts;
using Essence.Hours;

namespace Essence.UI.Services;

/// <summary>
/// A day of the hours editor: closed, or open from one time to the other ("HH:mm", as a time input gives it). A time set
/// here is the user's own; one the day took from an earlier day while it was blank (<see cref="HoursDraft.SetOpens"/>)
/// follows that day's until the user sets it here.
/// </summary>
public sealed class HoursDraftDay(DayOfWeek day)
{
    private string _opens = "";
    private string _closes = "";

    /// <summary>How it was shown, to tell whether it's still that: then it keeps <see cref="Stretches"/>.</summary>
    private (bool Closed, string Opens, string Closes)? _shown;

    public DayOfWeek Day { get; } = day;

    public bool Closed { get; set; }

    /// <summary>"06:00"; empty while not given.</summary>
    public string Opens
    {
        get => _opens;
        set => (_opens, OpensFrom) = (value, null);
    }

    /// <summary>"21:30"; earlier than <see cref="Opens"/> is the next day's ("22:00" to "02:00"), the same is all day.</summary>
    public string Closes
    {
        get => _closes;
        set => (_closes, ClosesFrom) = (value, null);
    }

    /// <summary>The day whose opening this one took while it was blank, and still follows; null once it's the user's own.</summary>
    public DayOfWeek? OpensFrom { get; private set; }

    /// <summary>Likewise for its closing.</summary>
    public DayOfWeek? ClosesFrom { get; private set; }

    /// <summary>
    /// It's open in several stretches in the hours it started from, and still as it was shown (from the first's start to
    /// the last's end): it keeps them. Changed, it's the one stretch it says.
    /// </summary>
    public bool KeepsStretches => IsAsShown && Stretches is { Count: > 1 };

    /// <summary>Still as the hours it started from showed it: closed, or with the times shown.</summary>
    internal bool IsAsShown => _shown == (Closed, _opens, _closes);

    /// <summary>Its stretches in the hours it started from (none when closed), which it saves while it's as shown; null for a blank day.</summary>
    internal IReadOnlyList<TimeRange>? Stretches { get; private set; }

    /// <summary>Shows the day as the hours it starts from have it: closed, or from <paramref name="opens"/> to <paramref name="closes"/>.</summary>
    internal void StartFrom(IReadOnlyList<TimeRange> stretches, string opens, string closes)
    {
        (Closed, _opens, _closes, Stretches) = (stretches.Count == 0, opens, closes, stretches);
        _shown = (Closed, _opens, _closes);
    }

    /// <summary>Takes <paramref name="day"/>'s opening, and follows it from now on.</summary>
    internal void FollowOpens(DayOfWeek day, string value) => (_opens, OpensFrom) = (value, day);

    /// <summary>Takes <paramref name="day"/>'s closing, and follows it from now on.</summary>
    internal void FollowCloses(DayOfWeek day, string value) => (_closes, ClosesFrom) = (value, day);
}

/// <summary>
/// The week the hours editor (HoursEditor.razor) shows and builds a rule from: seven days, Monday first, each closed or open
/// from one time to another, or open 24 hours every day. Pure, for the tests: what the editor shows comes from
/// <see cref="From"/>, and what it saves from <see cref="TryBuild"/> (<see cref="OpeningHours.FromDays"/>).
/// </summary>
public sealed class HoursDraft
{
    private HoursDraft(IReadOnlyList<HoursDraftDay> days, bool alwaysOpen, IReadOnlyList<TimeRange>? holidays)
    {
        Days = days;
        AlwaysOpen = alwaysOpen;
        Holidays = holidays;
    }

    /// <summary>Monday first.</summary>
    public IReadOnlyList<HoursDraftDay> Days { get; }

    /// <summary>"Open 24 hours": every day, holidays included, whatever the days say.</summary>
    public bool AlwaysOpen { get; set; }

    /// <summary>The holidays' hours of the hours shown, which the editor doesn't show: they're kept as they were.</summary>
    public IReadOnlyList<TimeRange>? Holidays { get; }

    /// <summary>
    /// The week as the station's hours shown say it, to start from: a closed day ticked closed, a day open all day from
    /// 00:00 to 00:00, a day open in several stretches from the first's start to the last's end (left as it is, it keeps
    /// them: <see cref="HoursDraftDay.KeepsStretches"/>). Unknown hours give blank days, to fill.
    /// </summary>
    public static HoursDraft From(OpeningHours? hours)
    {
        var days = new List<HoursDraftDay>(7);
        foreach (var day in Week)
        {
            var draft = new HoursDraftDay(day);
            if (hours is not null)
            {
                var ranges = hours.On(day);
                draft.StartFrom(
                    ranges,
                    ranges.Count == 0 ? "" : Clock(ranges[0].StartMinute),
                    ranges.Count == 0 ? "" : Clock(ranges[^1].EndMinute));
            }

            days.Add(draft);
        }

        return new HoursDraft(days, hours?.IsAlwaysOpen ?? false, hours is { IsAlwaysOpen: false } ? hours.OnHolidays : null);
    }

    /// <summary>
    /// Sets when a day opens, and when the days after it whose opening is still blank open too (up to the first closed one,
    /// or one already set): a week typed from Monday is typed once when every day is the same. Those days follow it until
    /// their own time is set: a time box tells each keystroke that makes a time (21:03, then 21:30, typing 2130), and they
    /// keep the last.
    /// </summary>
    public void SetOpens(DayOfWeek day, string value) =>
        Set(day, value, d => d.Opens, d => d.OpensFrom, (d, v) => d.Opens = v, (d, v) => d.FollowOpens(day, v));

    /// <summary>Sets when a day closes, and likewise the days after it whose closing is still blank, or follows it.</summary>
    public void SetCloses(DayOfWeek day, string value) =>
        Set(day, value, d => d.Closes, d => d.ClosesFrom, (d, v) => d.Closes = v, (d, v) => d.FollowCloses(day, v));

    /// <summary>Some day is open in several stretches, which it keeps while it's left as it is (the editor says so).</summary>
    public bool KeepsStretches => !AlwaysOpen && Days.Any(d => d.KeepsStretches);

    /// <summary>
    /// The hours set: open 24/7, or each day's (a closing time before the opening is the next day's, the same time is all
    /// day; a day left as it was shown, its stretches as they were), with the holidays' hours as they were. False with the
    /// first day whose times aren't both given.
    /// </summary>
    public bool TryBuild([NotNullWhen(true)] out OpeningHours? hours, out DayOfWeek? invalidDay)
    {
        hours = null;
        invalidDay = null;
        if (AlwaysOpen)
        {
            hours = OpeningHours.AlwaysOpen;
            return true;
        }

        var week = new Dictionary<DayOfWeek, IReadOnlyList<TimeRange>>();
        foreach (var day in Days)
        {
            if (day is { IsAsShown: true, Stretches: { } stretches })
            {
                // Only what the user changed changes: a day open in several stretches keeps them.
                week[day.Day] = stretches;
                continue;
            }

            if (day.Closed)
            {
                week[day.Day] = [];
                continue;
            }

            if (Minute(day.Opens) is not { } start || Minute(day.Closes) is not { } end)
            {
                invalidDay = day.Day;
                return false;
            }

            week[day.Day] = [Range(start, end)];
        }

        hours = OpeningHours.FromDays(week, Holidays);
        return true;
    }

    /// <summary>
    /// Whether a correction may go to OpenStreetMap too, and whether "Also update OpenStreetMap" starts ticked, for hours
    /// <paramref name="edited"/> against the server's (<paramref name="server"/>, not the device's). Hours the user changed
    /// may, ticked. Hours the same as a brand's (any source but OpenStreetMap's own) are brand data, never sent on their own:
    /// only once the user says they checked them at the station. The same as OpenStreetMap's, sending only says when they
    /// were checked: it starts unticked, unless they were.
    /// </summary>
    public static (bool Allowed, bool Ticked) OsmChoice(StationHours? server, OpeningHours edited, bool surveyed)
    {
        if (!SameAs(server, edited))
        {
            return (true, true);
        }

        return IsBrand(server!) ? (surveyed, surveyed) : (true, surveyed);
    }

    /// <summary>The hours are a brand's, or Essence's own: the same as them, they're not the user's to send.</summary>
    public static bool IsBrand(StationHours hours) => hours.Source != "osm";

    /// <summary>The hours edited say what the server's do (however either is written).</summary>
    public static bool SameAs(StationHours? server, OpeningHours edited) =>
        server is not null && OpeningHours.TryParse(server.Rule, lenient: true, out var shown, out _) && shown == edited;

    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>
    /// Sets the day's time, the user's own now; then the blank days right after it (up to a closed one, or one set) take it
    /// and follow it, and so do the days that already did, wherever they are.
    /// </summary>
    private void Set(
        DayOfWeek day, string value, Func<HoursDraftDay, string> get, Func<HoursDraftDay, DayOfWeek?> from,
        Action<HoursDraftDay, string> set, Action<HoursDraftDay, string> follow)
    {
        var index = Days.ToList().FindIndex(d => d.Day == day);
        if (index < 0)
        {
            return;
        }

        set(Days[index], value);
        var filling = true;
        foreach (var later in Days.Skip(index + 1))
        {
            var follows = from(later) == day;
            filling &= !later.Closed && (follows || get(later).Length == 0);
            if (filling || follows)
            {
                follow(later, value);
            }
        }
    }

    /// <summary>
    /// A day's stretch: a closing time before the opening is the next day's ("22:00" to "02:00", midnight included), and the
    /// same time is all day.
    /// </summary>
    private static TimeRange Range(int start, int end) =>
        end == start ? TimeRange.AllDay
        : end < start ? new TimeRange(start, end + TimeRange.MinutesPerDay)
        : new TimeRange(start, end);

    /// <summary>"06:00" (or with seconds, "06:00:00") as minutes of the day; null when it isn't a time.</summary>
    private static int? Minute(string text) =>
        TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.Hour * 60 + time.Minute
            : null;

    /// <summary>"06:00"; midnight, at either end of a day, is "00:00".</summary>
    private static string Clock(int minute) =>
        string.Create(CultureInfo.InvariantCulture, $"{minute % TimeRange.MinutesPerDay / 60:00}:{minute % 60:00}");
}
