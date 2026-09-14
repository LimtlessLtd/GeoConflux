using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.Application.Conflicts;

/// <summary>Decides which conflicts a report belongs to, from what the report states about itself.</summary>
public interface IConflictAssigner
{
    ConflictAssignment Assign(ConflictCandidate candidate);
}

/// <summary>
/// Assigns reports into the register deterministically, before any model is asked.
/// <para>
/// The order is the same one the pipeline already uses for classification and for placement: what the
/// source states beats what a heuristic works out, which beats what a model reads. A UCDP record
/// names its own conflict and there is nothing to decide; a report naming the Houthi movement
/// identifies a party and needs no model either. What is left over — a wire story naming a town and
/// nobody — is what a model is genuinely better at, and it is handled separately.
/// </para>
/// <para>
/// The rule this whole class turns on: <b>identity assigns, geography only narrows.</b> Naming a
/// party to a conflict is a claim about which conflict this is. Being inside its geography is not,
/// because conflicts overlap in space constantly — and so geography assigns only where it leaves
/// exactly one answer, and otherwise reports the choice it could not make.
/// </para>
/// </summary>
public sealed class ConflictAssigner(IConflictRegister register) : IConflictAssigner
{
    public ConflictAssignment Assign(ConflictCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var matches = new List<Match>();

        foreach (var conflict in register.All)
        {
            // A conflict a model proposed and nothing has corroborated may not take members. It is
            // shown and labelled, and until a second source supports it, it structures nothing —
            // the same rule a single social post is held under, for the same reason.
            if (!conflict.IsAsserted)
            {
                continue;
            }

            var basis = conflict.Admits(candidate);

            if (basis != ConflictMatchBasis.None)
            {
                matches.Add(new Match(conflict, basis));
            }
        }

        if (matches.Count == 0)
        {
            return ConflictAssignment.Nothing(NothingMatched(candidate));
        }

        var strongest = matches.Max(match => match.Basis);
        var best = matches.Where(match => match.Basis == strongest).ToArray();
        var weaker = matches.Where(match => match.Basis != strongest).ToArray();

        return strongest switch
        {
            ConflictMatchBasis.Coded => new ConflictAssignment(Memberships(best), Memberships(weaker), null),
            ConflictMatchBasis.Actor => ByIdentity(candidate, best, weaker),
            _ => ByGeography(best, weaker, strongest),
        };
    }

    /// <summary>
    /// Settles between conflicts the report names a party to, by how much of the party's name it
    /// actually used.
    /// <para>
    /// Necessary because party names share words. A report naming the Jalisco Cartel New Generation
    /// shares four words with the conflict it means and one — "cartel" — with every cartel conflict
    /// in the register. Taking the conflicts that share the most words picks the right one without
    /// anybody having to decide in advance which words are meaningful, and a report that genuinely
    /// names nothing more specific than "cartel" ties across twenty conflicts and is reported as the
    /// ambiguity it is rather than being assigned to the first eight.
    /// </para>
    /// </summary>
    private static ConflictAssignment ByIdentity(
        ConflictCandidate candidate,
        IReadOnlyList<Match> named,
        IReadOnlyList<Match> weaker)
    {
        var scored = named
            .Select(match => (match, Words: match.Conflict.ActorOverlap(candidate.ActorNames)))
            .ToArray();

        var most = scored.Max(entry => entry.Words);
        var best = scored.Where(entry => entry.Words == most).Select(entry => entry.match).ToArray();
        var rest = scored.Where(entry => entry.Words != most).Select(entry => entry.match).ToArray();

        // One word shared with several conflicts is a category, not a name.
        //
        // This is the same argument that removes "government" and "civilians" from the register,
        // applied at match time and at a threshold of one instead of a third. It is here because the
        // first real run found it: reports about a vessel struck off Qeshm Island were being assigned
        // to four Iranian conflicts at once — including Iran's conflict with Islamic State — on the
        // strength of the word "Iran", which appears in all four party lists because it is the name
        // of the country rather than of anybody fighting.
        //
        // Multi-membership survives, because it never depended on this: a report naming the "Houthi
        // movement" shares two words with each conflict that movement is a party to, and belongs to
        // both. What is refused is asserting membership from a single word several conflicts share.
        if (best.Length > 1 && most < MinimumSharedWords)
        {
            return new ConflictAssignment([], Memberships([.. best, .. rest, .. weaker]), NamedNobody(best.Length));
        }

        // And a name several conflicts share every word of is still not an answer past a handful.
        // Truncating dozens to the first few would present an arbitrary slice as a finding.
        return best.Length > ConflictAssignment.MaxMemberships
            ? new ConflictAssignment([], Memberships(best), TooManyNamed(best.Length, most))
            : new ConflictAssignment(Memberships(best), Memberships([.. rest, .. weaker]), null);
    }

    /// <summary>
    /// How many words a report must share with a party name before that name may be read as
    /// identifying more than one conflict at once. One word is a category; two is a name.
    /// </summary>
    private const int MinimumSharedWords = 2;

    private static string NamedNobody(int count) =>
        $"This names one thing {count} conflicts have in common and nothing that separates them, "
        + "which is a word about where rather than the name of a party.";

    /// <summary>
    /// Geography, and only geography. One answer settles it; several do not, and the honest output of
    /// "it happened somewhere four wars are being fought" is that sentence rather than a quarter-share
    /// in each.
    /// </summary>
    private static ConflictAssignment ByGeography(
        IReadOnlyList<Match> best,
        IReadOnlyList<Match> weaker,
        ConflictMatchBasis basis) =>
        best.Count == 1
            ? new ConflictAssignment(Memberships(best), Memberships(weaker), null)
            : new ConflictAssignment([], Memberships([.. best, .. weaker]), Undecided(best.Count, basis));

    /// <summary>
    /// Trims a list for display and orders it the way a reader would want it: largest conflict first,
    /// because that is the one they are most likely to be looking for.
    /// </summary>
    private static IReadOnlyList<ConflictMembership> Memberships(IReadOnlyList<Match> matches) =>
    [
        .. matches
            .OrderByDescending(match => match.Basis)
            .ThenByDescending(match => match.Conflict.CodedEvents)
            .Take(ConflictAssignment.MaxListed)
            .Select(match => new ConflictMembership(match.Conflict.Key, match.Conflict.Name, match.Basis)),
    ];

    private static string TooManyNamed(int count, int words) => words == 1
        ? $"The one word this report shares with a party name appears in {count} conflicts, which "
          + "makes it a word about conflict rather than the name of anybody in one."
        : $"This names something {count} conflicts share, and nothing that separates them.";

    private static string Undecided(int count, ConflictMatchBasis basis) => basis == ConflictMatchBasis.Place
        ? $"{count} conflicts have had coded events at this place and the report names a party to none "
          + "of them, so which one this belongs to is not established."
        : $"This was placed in a country {count} conflicts are coded in, and the report names a party "
          + "to none of them. The country is not enough to say which.";

    private static string NothingMatched(ConflictCandidate candidate) => candidate switch
    {
        { CodedConflictKey: not null } =>
            "The source coded this into a conflict the register does not hold. The register is only "
            + "as current as the extract it was built from, so a conflict coded since then is absent "
            + "rather than rejected.",

        { CountryCode: null, PlaceName: null } =>
            "This was never placed, and it names no party to a conflict in the register. Assigning it "
            + "would mean guessing.",

        _ =>
            "No conflict in the register has coded events where this was placed, and it names no party "
            + "to one. The register covers the conflicts one coding project could source events for; "
            + "absence from it is not absence of conflict.",
    };

    private readonly record struct Match(Conflict Conflict, ConflictMatchBasis Basis);
}
