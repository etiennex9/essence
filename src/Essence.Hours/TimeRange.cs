using System.Globalization;

namespace Essence.Hours;

/// <summary>When a station is open on a day, in minutes from that day's local midnight.</summary>
/// <param name="StartMinute">From 0 (00:00) to 1439 (23:59).</param>
/// <param name="EndMinute">
/// After the start: up to 1440 (24:00) on the day itself, or past it for a range that spills into the next day
/// ("22:00-02:00" is 1320 to 1560), as long as it lasts less than 24 hours.
/// </param>
public readonly record struct TimeRange(int StartMinute, int EndMinute)
{
    public const int MinutesPerDay = 24 * 60;

    /// <summary>The whole day, 00:00-24:00.</summary>
    public static TimeRange AllDay => new(0, MinutesPerDay);

    /// <summary>It ends after midnight, on the next day.</summary>
    public bool Spills => EndMinute > MinutesPerDay;

    /// <summary>
    /// The range can be written "HH:MM-HH:MM": it starts on its day, and either ends by 24:00 or spills less than a whole
    /// day, since "22:00-22:00" would be no time at all.
    /// </summary>
    public bool IsValid =>
        StartMinute is >= 0 and < MinutesPerDay
        && EndMinute > StartMinute
        && (EndMinute <= MinutesPerDay || EndMinute - StartMinute < MinutesPerDay);

    /// <summary>"06:00-21:30", "22:00-24:00", or for a spill the next day's clock time, "22:00-02:00".</summary>
    public override string ToString() =>
        $"{Clock(StartMinute)}-{(EndMinute == MinutesPerDay ? "24:00" : Clock(EndMinute % MinutesPerDay))}";

    private static string Clock(int minute) => string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");
}
