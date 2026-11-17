using Essence.Hours;

namespace Essence.Ranking;

/// <summary>A station's hours as the ranking reads them: the rule, in the station's time zone, and how far it's trusted.</summary>
/// <param name="Trusted">
/// Sure enough to leave the station out when it's closed (<see cref="ChosenHours.Trusted"/>): a brand's or Essence's, from a
/// source that isn't stale. OpenStreetMap's, and a stale source's, are only shown.
/// </param>
/// <param name="TemporarilyClosed">The source says the station is closed for now, whatever its hours.</param>
public sealed record ArrivalHours(OpeningHours Hours, TimeZoneInfo Zone, bool Trusted, bool TemporarilyClosed);

/// <summary>
/// Whether a station is open when the driver gets there, and the stations a recommendation leaves out for it: those its
/// trusted hours say will be closed on arrival, or that are closed for now. Best near you and Best on the way leave them out
/// before ranking (<see cref="NearYou.Rank"/>, <see cref="OnTheWay.Rank"/>), so the closest, or the first on the way, that
/// the ranking counts the savings against is a station the driver can fill up at.
/// </summary>
public static class Arrivals
{
    /// <summary>Closing within this after the arrival is closing soon: the driver is warned (the owner's choice).</summary>
    public static readonly TimeSpan ClosesSoonWithin = TimeSpan.FromMinutes(30);

    /// <summary>Whether the station is open at <paramref name="arriveAt"/>, closes soon after, or is closed then.</summary>
    public static ArrivalCheck Check(OpeningHours hours, TimeZoneInfo zone, DateTimeOffset arriveAt) =>
        HoursEvaluator.OnArrival(hours, zone, arriveAt, ClosesSoonWithin);

    /// <summary>
    /// The candidates a recommendation keeps, and those it leaves out. A station is left out only when its hours are
    /// <see cref="ArrivalHours.Trusted"/> and it's closed for now, or closed at its arrival on a day whose hours hold (no
    /// <see cref="ArrivalCheck.HolidayCaution"/>), and its id isn't in <paramref name="ignoredIds"/> (hours the user
    /// corrected on the device). Unknown hours, OpenStreetMap's, a stale source's and a holiday's are always kept. When
    /// every candidate would be left out, all are kept: a closed station is still better than none, and the app says so.
    /// </summary>
    /// <param name="hoursOf">A candidate's hours; null when they're unknown.</param>
    /// <param name="arriveAtOf">When the driver would get to it.</param>
    /// <returns>Both in the candidates' order.</returns>
    public static (List<T> Kept, List<T> LeftOut) LeaveOutClosed<T>(
        IReadOnlyCollection<T> candidates,
        Func<T, ArrivalHours?> hoursOf,
        Func<T, DateTimeOffset> arriveAtOf,
        IReadOnlySet<long> ignoredIds,
        Func<T, long> idOf)
    {
        var kept = new List<T>(candidates.Count);
        var leftOut = new List<T>();
        foreach (var candidate in candidates)
        {
            (IsClosed(hoursOf(candidate), arriveAtOf(candidate)) && !ignoredIds.Contains(idOf(candidate)) ? leftOut : kept).Add(candidate);
        }

        return kept.Count == 0 ? ([.. candidates], []) : (kept, leftOut);
    }

    /// <summary>
    /// Trusted hours that say the station is closed for now, or closed at the arrival with no holiday caution: what
    /// <see cref="LeaveOutClosed"/> leaves out (unless ignored, or everything is).
    /// </summary>
    public static bool IsClosed(ArrivalHours? hours, DateTimeOffset arriveAt)
    {
        if (hours is not { Trusted: true })
        {
            return false;
        }

        if (hours.TemporarilyClosed)
        {
            return true;
        }

        var check = Check(hours.Hours, hours.Zone, arriveAt);
        return check.State == Arrival.Closed && !check.HolidayCaution;
    }
}
