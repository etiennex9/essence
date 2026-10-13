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

    /// <summary>The unit and count for "… ago": minutes under an hour, hours under two days, then days.</summary>
    public static (AgoUnit Unit, int Value) Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => (AgoUnit.JustNow, 0),
        { TotalHours: < 1 } => (AgoUnit.Minutes, (int)elapsed.TotalMinutes),
        { TotalHours: < 48 } => (AgoUnit.Hours, (int)elapsed.TotalHours),
        _ => (AgoUnit.Days, (int)elapsed.TotalDays),
    };
}
