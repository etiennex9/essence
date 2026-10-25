using System.Globalization;

namespace Essence.UI.Services;

public enum AgoUnit
{
    JustNow,
    Minutes,
    Hours,
    Days,
}

public static class Formatting
{
    /// <summary>Cents per litre with one decimal in the culture's style ("164.9", "164,9"), or a dash when there's no price.</summary>
    public static string Price(decimal? cents, IFormatProvider culture) => cents is { } c ? c.ToString("0.0", culture) : "—";

    /// <summary>
    /// Dollars to the cent in the culture's style, with the dollar sign where the culture puts its currency symbol: "$64.75",
    /// "64,75 $". Plain "C" would show £ or € to a browser set to British English or France's French.
    /// </summary>
    public static string Money(decimal dollars, IFormatProvider culture)
    {
        var format = (NumberFormatInfo)NumberFormatInfo.GetInstance(culture).Clone();
        format.CurrencySymbol = "$";
        return dollars.ToString("C2", format);
    }

    /// <summary>Kilometres with one decimal in the culture's style ("9.0", "9,0").</summary>
    public static string Km(double km, IFormatProvider culture) => km.ToString("0.0", culture);

    /// <summary>A drive's length to the minute: "22 min", "2 h 35 min", "3 h" (the same in French).</summary>
    public static string Duration(double minutes, IFormatProvider culture)
    {
        var total = (int)Math.Round(Math.Max(0, minutes));
        var (hours, rest) = (total / 60, total % 60);
        return hours == 0
            ? string.Create(culture, $"{rest} min")
            : rest == 0 ? string.Create(culture, $"{hours} h") : string.Create(culture, $"{hours} h {rest} min");
    }

    /// <summary>The time of day in the culture's short style ("8:10 a.m.", "08 h 10"), in the device's time zone.</summary>
    public static string TimeOfDay(DateTimeOffset at, TimeZoneInfo zone, IFormatProvider culture) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("t", culture);

    /// <summary>The unit and count for "… ago": minutes under an hour, hours under two days, then days.</summary>
    public static (AgoUnit Unit, int Value) Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => (AgoUnit.JustNow, 0),
        { TotalHours: < 1 } => (AgoUnit.Minutes, (int)elapsed.TotalMinutes),
        { TotalHours: < 48 } => (AgoUnit.Hours, (int)elapsed.TotalHours),
        _ => (AgoUnit.Days, (int)elapsed.TotalDays),
    };
}
