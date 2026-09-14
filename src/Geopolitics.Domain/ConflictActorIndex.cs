namespace Geopolitics.Domain;

/// <summary>
/// Decides which words in the register's party names actually identify anybody.
/// <para>
/// A conflict on its own cannot know this. "Government" is in the name of nearly every state party
/// UCDP codes, "forces" and "front" are in dozens, and "houthi" is in one — and those are facts about
/// the register rather than about any entry in it. Matching on a word shared by two hundred conflicts
/// would assign every report naming a government to two hundred conflicts, which is not a subtle
/// failure: it is the whole register collapsing into one bucket.
/// </para>
/// <para>
/// So the words are counted and the common ones discarded. No stopword list, because a hand-written
/// one would encode somebody's idea of which words are meaningful in conflict naming, would be wrong
/// in languages nobody checked, and would have to be maintained as the register grows. Counting is
/// none of those things and it is right by construction.
/// </para>
/// </summary>
public static class ConflictActorIndex
{
    /// <summary>
    /// The share of the register a word must name parties in before it stops identifying any of them.
    /// <para>
    /// A tenth, and the register's own numbers are why. Counted across the 2024 coding, exactly two
    /// words appear in more than a tenth of the conflicts — "government" in 117 of 319 and "civilians"
    /// in 103 — and then there is a cliff to 22. Those two are furniture: they describe what kind of
    /// party somebody is rather than which party. Everything below the cliff is a mixture that no
    /// threshold can separate, because "cartel" appears in 19 conflicts and "fulani" in 13 and only
    /// one of those is a name. Which is why this filter deliberately does not try: it removes the two
    /// words that would match everything, and how many words a report shares with each conflict sorts
    /// out the rest.
    /// </para>
    /// </summary>
    public const double AmbiguousShare = 0.1;

    /// <summary>
    /// Smallest register this filter will act on at all. A share is meaningless over three entries —
    /// a tenth of three is nothing, so every word would be ubiquitous and every conflict would end up
    /// with no identifying words whatsoever. Below this the register is small enough that a reader
    /// can see what is in it, which is a better safeguard than a statistic computed from nothing.
    /// </summary>
    public const int SmallestMeasurableRegister = 40;

    /// <summary>
    /// Counts the words across the register and removes the ubiquitous ones from every conflict that
    /// carries them. Returns the words removed, so a caller can log or report them rather than having
    /// to take on trust that the filter did something sensible.
    /// </summary>
    public static IReadOnlyList<string> Prune(IReadOnlyList<Conflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);

        if (conflicts.Count < SmallestMeasurableRegister)
        {
            return [];
        }

        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var conflict in conflicts)
        {
            foreach (var token in conflict.ActorTokens)
            {
                frequency[token] = frequency.GetValueOrDefault(token) + 1;
            }
        }

        var limit = conflicts.Count * AmbiguousShare;

        var ambiguous = frequency
            .Where(entry => entry.Value > limit)
            .Select(entry => entry.Key)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();

        foreach (var conflict in conflicts)
        {
            foreach (var token in ambiguous)
            {
                conflict.DropAmbiguousActorToken(token);
            }
        }

        return ambiguous;
    }
}
