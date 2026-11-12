#if DEBUG
using System.Globalization;
using System.Text.RegularExpressions;

namespace Essence.Web;

/// <summary>
/// Development only (<c>Program.cs</c>): the app's clock started at another moment, moving on with the real one, to check
/// stations' hours by hand at another time of day, with <c>?now=2026-11-11T21:15:00-05:00</c> on the page's address.
/// Timers wait as long as they would: only the time read moves.
/// </summary>
internal sealed partial class SimulatedTime(DateTimeOffset start) : TimeProvider
{
    private readonly TimeSpan _offset = start - TimeProvider.System.GetUtcNow();

    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + _offset;

    public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

    /// <summary>The address's <c>now</c>: ISO 8601 with its offset (<c>Z</c>, <c>-05:00</c>); null without one, or when it isn't that.</summary>
    public static DateTimeOffset? Parse(string uri)
    {
        foreach (var pair in new Uri(uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0 || pair[..equals] != "now")
            {
                continue;
            }

            var text = Uri.UnescapeDataString(pair[(equals + 1)..]);
            return WithOffset().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at
                : null;
        }

        return null;
    }

    /// <summary>A date and time with its offset: without one, the moment would depend on the device's zone.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex WithOffset();
}
#endif
