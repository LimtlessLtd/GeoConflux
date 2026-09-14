using System.Text.RegularExpressions;
using Geopolitics.Domain;

namespace Geopolitics.Application.Control;

/// <summary>
/// Finds the reports that are about who <em>holds</em> a place, in ordinary prose.
/// <para>
/// Deterministic, and it runs before any model does, which is [ADR 011]'s ordering applied to a new
/// question. A wire report saying a town was captured is the commonest control evidence there is and
/// the only kind available without a dataset credential, so it is worth doing properly rather than
/// handing straight to a classifier.
/// </para>
/// <para>
/// The hard part is not finding the verbs. It is refusing everything that uses them about something
/// other than ground: <em>captured a soldier near Kherson</em>, <em>seized a shipment bound for
/// Aden</em>, <em>abandoned an attempt on Mykolaiv</em>. So the place has to be the thing the verb
/// acts on, not merely nearby, and where that cannot be established this returns nothing rather than
/// its best guess.
/// </para>
/// </summary>
public static class ControlClaimDetector
{
    /// <summary>
    /// Untrusted text from an open feed, so the matcher is given a deadline. The patterns below have
    /// no nested quantifiers and the only interpolated part is escaped, which makes catastrophic
    /// backtracking unreachable rather than merely unlikely — this is the belt to that braces.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>
    /// Nouns a report puts between the verb and the name — "captured the town of Avdiivka". Allowed
    /// because they confirm the object is ground rather than weakening the match: every one of them
    /// is a settlement or a piece of terrain.
    /// </summary>
    private const string Filler = @"(?:\s+(?:the|a|an|of|city|town|village|settlement|district|port|airfield|border\s+crossing))*";

    private static readonly string[] GainVerbs =
        ["captured", "recaptured", "retook", "seized", "overran", "liberated", "took control of", "took over"];

    private static readonly string[] WithdrawalVerbs =
        ["withdrew from", "pulled out of", "pulled back from", "abandoned", "ceded", "lost control of", "retreated from"];

    /// <summary>
    /// Passive and stative forms, where the place leads. Kept separate because the word order is
    /// reversed and folding both into one pattern would match the active verbs at any distance.
    /// </summary>
    private static readonly string[] GainPredicates =
        [
            "fell to", "has fallen to", "was captured by", "was seized by", "was taken by",
            "has been captured by", "has been seized by", "is now held by", "is now under the control of",
        ];

    /// <summary>
    /// What this report claims about control of <paramref name="placeName"/>, and who by.
    /// </summary>
    /// <returns>
    /// <see cref="ControlSignal.None"/> and a null actor whenever anything is uncertain — no place,
    /// no matching phrase, or no single actor the claim could be about. Returning nothing is the
    /// common case and the safe direction: an unattributed or misattributed control claim is worse
    /// than an absent one, because the absent one does not get drawn.
    /// </returns>
    public static (ControlSignal Signal, string? Actor) Detect(
        string? text,
        string? placeName,
        IEnumerable<ExtractedEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(placeName))
        {
            return (ControlSignal.None, null);
        }

        var signal = Match(text, placeName);

        if (signal == ControlSignal.None)
        {
            return (ControlSignal.None, null);
        }

        // Exactly one candidate actor, or nothing. Two named parties in one sentence is precisely
        // the case a rule cannot settle — "Northern Forces recaptured the town from Southern
        // Militia" and "Southern Militia recaptured it from Northern Forces" differ by word order
        // alone — and guessing between them would attribute a place to whoever was mentioned first.
        var actors = entities
            .Where(entity => entity.Type is EntityType.Organisation or EntityType.State or EntityType.MilitaryUnit)
            .Select(entity => entity.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return actors.Count == 1
            ? (signal, actors[0])
            : (ControlSignal.None, null);
    }

    /// <summary>
    /// Whether the prose puts this place on the receiving end of a control verb.
    /// </summary>
    /// <remarks>
    /// Withdrawal is tested first. "Forces withdrew from Kherson after recapturing it in March" is a
    /// withdrawal report that also contains a gain verb, and reading the gain would invert what the
    /// report says. Where a report carries both, the one it is actually about is the leaving.
    /// </remarks>
    private static ControlSignal Match(string text, string placeName)
    {
        var place = Regex.Escape(placeName.Trim());

        // Bounded with lookarounds rather than with \b. A place name is as likely to end in
        // punctuation as in a letter — "Sana'a (Old City)" — and \b asserts nothing between two
        // non-word characters, so the match would fail on exactly the awkward names and succeed on
        // the easy ones, which is the worst way for a rule like this to be wrong.
        if (Matches(text, $@"(?<!\w)(?:{Alternation(WithdrawalVerbs)}){Filler}\s+{place}(?!\w)"))
        {
            return ControlSignal.WithdrawalReported;
        }

        if (Matches(text, $@"(?<!\w)(?:{Alternation(GainVerbs)}){Filler}\s+{place}(?!\w)")
            || Matches(text, $@"(?<!\w){place}\s+(?:{Alternation(GainPredicates)})(?!\w)"))
        {
            return ControlSignal.TerritoryTransferred;
        }

        return ControlSignal.None;
    }

    private static string Alternation(IEnumerable<string> phrases) =>
        string.Join('|', phrases.Select(phrase => Regex.Escape(phrase).Replace(@"\ ", @"\s+", StringComparison.Ordinal)));

    private static bool Matches(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, Options, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            // A report that cannot be matched inside the deadline is not a report about control.
            // Failing closed costs one claim; failing open costs the request.
            return false;
        }
    }
}
