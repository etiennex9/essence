namespace Essence.Ranking;

public static class Eligibility
{
    /// <summary>The feed's status for an open station.</summary>
    public const string Operating = "En opération";

    /// <summary>
    /// Whether a station can be recommended for a fuel: it's operating, its price for that fuel is available, and a
    /// members-only station (Costco) only to members.
    /// </summary>
    public static bool IsEligible(string status, bool isAvailable, decimal? priceCents, bool membersOnly, bool isMember) =>
        status == Operating && isAvailable && priceCents is not null && (!membersOnly || isMember);
}
