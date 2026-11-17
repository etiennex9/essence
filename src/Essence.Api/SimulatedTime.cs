using System.Globalization;
using System.Text.RegularExpressions;

namespace Essence.Api;

/// <summary>
/// Development only: the API's clock started at another moment (<c>Clock:Now</c>, ISO 8601 with its offset, e.g.
/// <c>2026-11-11T21:15:00-05:00</c>), moving on with the real one, to check by hand which stations Best near you and Best on
/// the way leave out at another time of day, as Essence.Web's <c>?now=</c> does for the app. Timers wait as long as they
/// would: only the time read moves.
/// </summary>
public sealed partial class SimulatedTime(DateTimeOffset start, TimeProvider real) : TimeProvider
{
    public const string ConfigKey = "Clock:Now";

    private readonly TimeSpan _offset = start - real.GetUtcNow();

    public override DateTimeOffset GetUtcNow() => real.GetUtcNow() + _offset;

    public override TimeZoneInfo LocalTimeZone => real.LocalTimeZone;

    /// <summary>
    /// The API's clock: the system's, unless the environment is Development and <c>Clock:Now</c> is set; elsewhere it's
    /// ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">In Development, <c>Clock:Now</c> isn't a moment with its offset.</exception>
    public static TimeProvider For(bool isDevelopment, string? now, TimeProvider? real = null)
    {
        real ??= System;
        if (!isDevelopment || string.IsNullOrWhiteSpace(now))
        {
            return real;
        }

        return WithOffset().IsMatch(now.Trim()) && DateTimeOffset.TryParse(now.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? new SimulatedTime(start, real)
            : throw new InvalidOperationException(
                $"{ConfigKey} must be a date and time with its offset, like 2026-11-11T21:15:00-05:00 (got \"{now}\").");
    }

    /// <summary>A date and time with its offset: without one, the moment would depend on the machine's zone.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex WithOffset();
}
