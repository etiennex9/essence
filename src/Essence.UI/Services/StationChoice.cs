using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>What the hero says of the station it shows (BestPanel): how it compares with the reference.</summary>
public enum HeroCase
{
    /// <summary>It saves at least $0.25 and could be picked: the pick, or another that saves less.</summary>
    Saves,

    /// <summary>It saves at least $0.25, but it's past the longest detour.</summary>
    SavesOverDetour,

    /// <summary>It saves at least $0.25 by the estimate, but by road it might not.</summary>
    SavesUncertain,

    /// <summary>Within $0.25 of the reference, either way.</summary>
    AboutTheSame,

    /// <summary>It costs at least $0.25 more than the reference.</summary>
    CostsMore,

    /// <summary>What the savings are counted against, while another station is the pick.</summary>
    Reference,

    /// <summary>On a trip, the range doesn't get there.</summary>
    PastRange,

    /// <summary>The pick is the reference: those that save more are past the longest detour.</summary>
    PickIsReferenceDetour,

    /// <summary>The pick is the reference: those that save more might not by road.</summary>
    PickIsReferenceUncertain,

    /// <summary>The pick is the reference, being the only station.</summary>
    PickIsReferenceOnly,

    /// <summary>The pick is the reference: no other station saves $0.25 or more.</summary>
    PickIsReferenceNothingSaves,

    /// <summary>Near you, nothing within 25 km has a price: the pick is the closest one at any distance.</summary>
    PickBeyondRadius,
}

/// <summary>
/// The station Best near you and Best on the way show on top (the hero): the pick, or one the user chose among those the
/// pane lists. A choice lasts while the same question is answered again and the station is still listed; otherwise it's
/// the pick again.
/// </summary>
public static class StationChoice
{
    /// <summary>Savings under this are "about the same" (Essence.Ranking's NearYou.SmallestSaving).</summary>
    public const decimal SmallestSaving = 0.25m;

    /// <summary>"Also worth it" shows at most this many stations.</summary>
    public const int MaxOthers = 3;

    /// <summary>
    /// The station chosen after a new answer: <paramref name="selectedId"/> if the answer is to the
    /// <paramref name="sameQuestion"/> and lists it, and it isn't the pick; otherwise null, the pick.
    /// </summary>
    public static long? Keep(long? selectedId, bool sameQuestion, long? pickId, IEnumerable<long> selectable) =>
        selectedId is { } id && sameQuestion && id != pickId && selectable.Contains(id) ? id : null;

    /// <summary>
    /// What Best near you lets the user choose: the pick, the closest and the cheapest (the tiles, shown with more than one
    /// station) and the stations "Also worth it". None without a pick.
    /// </summary>
    public static IEnumerable<long> Selectable(BestNearby result)
    {
        if (result.Pick is not { } pick)
        {
            return [];
        }

        List<long> ids = [pick.Id];
        if (result.Stations.Count > 1)
        {
            ids.AddRange(result.Stations.Where(s => s.Id == result.ReferenceId || s.Id == result.CheapestId).Select(s => s.Id));
        }

        ids.AddRange(Others(result, pick).Select(s => s.Id));
        return ids;
    }

    /// <summary>What Best on the way lets the user choose: every station listed, even when none is reachable.</summary>
    public static IEnumerable<long> Selectable(TripPlan plan) => plan.Stations.Select(s => s.Id);

    /// <summary>
    /// Other stations that save, besides the pick: those that could be picked first, then those past the longest detour or
    /// whose saving is uncertain, each lowest cost first.
    /// </summary>
    public static IReadOnlyList<RankedStation> Others(BestNearby result, RankedStation pick) =>
        result.Stations
            .Where(s => s.Id != pick.Id && s.SavesDollars >= SmallestSaving)
            .OrderBy(s => s.OverDetourLimit || s.SavingUncertain)
            .ThenBy(s => s.CostDollars)
            .Take(MaxOthers)
            .ToList();

    /// <summary>How a station near you compares: the pick (and why it's the closest, when it is), the closest, or by its saving.</summary>
    public static HeroCase CaseOf(RankedStation station, BestNearby result)
    {
        if (station.Id == result.Pick?.Id)
        {
            if (station.Id != result.ReferenceId)
            {
                return HeroCase.Saves;
            }

            if (result.RadiusKm is null)
            {
                return HeroCase.PickBeyondRadius;
            }

            if (result.Stations.FirstOrDefault(s => s.Id != station.Id && s.SavesDollars >= SmallestSaving) is { } blocked)
            {
                return blocked.OverDetourLimit ? HeroCase.PickIsReferenceDetour : HeroCase.PickIsReferenceUncertain;
            }

            return result.Stations.Count == 1 ? HeroCase.PickIsReferenceOnly : HeroCase.PickIsReferenceNothingSaves;
        }

        if (station.Id == result.ReferenceId)
        {
            return HeroCase.Reference;
        }

        return BySaving(station.SavesDollars, station.OverDetourLimit, station.SavingUncertain);
    }

    /// <summary>How a station on the way compares: past the range, the pick (and why it's the reference, when it is), the reference, or by its saving.</summary>
    public static HeroCase CaseOf(TripStation station, TripPlan plan)
    {
        if (!station.Reachable)
        {
            return HeroCase.PastRange;
        }

        if (station.Id == plan.PickId)
        {
            if (station.Id != plan.ReferenceId)
            {
                return HeroCase.Saves;
            }

            if (plan.Stations.Any(s => s.Id != station.Id && s.Reachable && s.SavesDollars >= SmallestSaving))
            {
                return HeroCase.PickIsReferenceDetour;
            }

            return plan.Stations.Count(s => s.Reachable) == 1 ? HeroCase.PickIsReferenceOnly : HeroCase.PickIsReferenceNothingSaves;
        }

        if (station.Id == plan.ReferenceId)
        {
            return HeroCase.Reference;
        }

        return BySaving(station.SavesDollars, station.OverDetourLimit, uncertain: false);
    }

    private static HeroCase BySaving(decimal savesDollars, bool overDetourLimit, bool uncertain) =>
        savesDollars >= SmallestSaving
            ? overDetourLimit ? HeroCase.SavesOverDetour : uncertain ? HeroCase.SavesUncertain : HeroCase.Saves
            : savesDollars <= -SmallestSaving ? HeroCase.CostsMore : HeroCase.AboutTheSame;
}
