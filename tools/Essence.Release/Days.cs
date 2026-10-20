using System.Globalization;
using System.Text.RegularExpressions;

namespace Essence.Release;

/// <summary>Release days: calendar days in Québec's time zone, and the <c>vYYYY-MM-DD</c> tags that mark them.</summary>
public static partial class Days
{
    /// <summary>America/Toronto, which Montréal follows (the IANA name works on Linux and on Windows).</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    public static DateOnly Of(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);

    public static DateOnly Today(TimeProvider time) => Of(time.GetUtcNow());

    /// <summary>"2026-10-19": how a version is written.</summary>
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string TagName(DateOnly day) => "v" + Iso(day);

    /// <summary>Exactly <c>vYYYY-MM-DD</c> with a real date: other tags are left alone.</summary>
    public static bool TryParseTag(string name, out DateOnly day)
    {
        day = default;
        return TagPattern().IsMatch(name)
               && DateOnly.TryParseExact(name.AsSpan(1), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    public static string Month(DateOnly day) => day.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^v\d{4}-\d{2}-\d{2}$")]
    private static partial Regex TagPattern();
}
