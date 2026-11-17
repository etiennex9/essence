using System.Globalization;

namespace Essence.Ingest.Hours;

/// <summary>
/// Whether a run's matches may replace the rows serving now. A source that answers with nothing, or with far fewer
/// stations than last time, is more likely broken (a blocked request, a changed format, a half-empty answer) than the
/// stations closed: it's rejected, and the last good rows keep serving. Rules shipped in the image are the owner's, and
/// fewer of them is the owner's choice: only one that can't be read stops them.
/// </summary>
public static class HoursGate
{
    /// <summary>The gate for a run of <paramref name="source"/>: <see cref="CheckShipped"/> or <see cref="Check(int, int, int?, double)"/>.</summary>
    /// <returns>Why the run is rejected, or null when it's accepted.</returns>
    public static string? Check(IHoursSource source, HoursParse parsed, int matched, int? lastGoodMatched, double minShareOfLastGood) =>
        source.ShippedInImage
            ? CheckShipped(parsed.Warnings.Count)
            : Check(parsed.Records.Count, matched, lastGoodMatched, minShareOfLastGood);

    /// <summary>A source that downloads its records.</summary>
    /// <param name="matched">
    /// The stations matched but for those matched by location only (<see cref="MatchResult.Counted"/>), as
    /// <c>hours_imports.matched</c> records them. Those carry no hours, and they'd hide a broken answer: elements that lost
    /// their brands would match by location what they matched by brand. Leaving them out, a run that gains them is no jump,
    /// and one that loses them no drop.
    /// </param>
    /// <param name="lastGoodMatched">The stations the last good run matched; null when there's none, and any number goes.</param>
    /// <returns>Why the run is rejected, or null when it's accepted.</returns>
    public static string? Check(int records, int matched, int? lastGoodMatched, double minShareOfLastGood)
    {
        if (records == 0)
        {
            return "The source gave no records.";
        }

        if (lastGoodMatched is { } last && matched < minShareOfLastGood * last)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{matched} stations matched, fewer than {minShareOfLastGood:P0} of the last good run's {last}.");
        }

        return null;
    }

    /// <summary>
    /// Rules shipped in the image (<see cref="IHoursSource.ShippedInImage"/>). However few there are, none included, they
    /// go in as written: nothing but a new image changes them, so rejecting fewer would keep the old ones serving until
    /// someone edits the database. A rule left out (one that can't be read, a brand that isn't one, a rule there twice) is
    /// a mistake in the file, though, and taking the rest would drop that rule's rows without a word: the file is
    /// rejected until it's fixed, and the rules before it keep serving.
    /// </summary>
    /// <param name="warnings">The rules left out (<see cref="HoursParse.Warnings"/>).</param>
    /// <returns>Why the run is rejected, or null when it's accepted.</returns>
    public static string? CheckShipped(int warnings) =>
        warnings == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{warnings} of the rules shipped were left out (see the warnings): the file needs fixing.");
}
