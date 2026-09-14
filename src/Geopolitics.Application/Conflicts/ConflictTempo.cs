namespace Geopolitics.Application.Conflicts;

/// <summary>What can be said about how a conflict's tempo has changed, which is often nothing.</summary>
public enum TempoVerdict
{
    /// <summary>
    /// Coverage changed enough that the comparison says more about this system than about the
    /// conflict. The case the whole design exists for, and the one a naive line gets exactly
    /// backwards.
    /// </summary>
    NotStated = 0,

    /// <summary>Nothing to compare against: this conflict produced no reports in the previous window.</summary>
    NoBaseline,

    /// <summary>Too few reports for a direction to mean anything, in one window or both.</summary>
    TooThin,

    Steady,

    Rising,

    Falling,
}

/// <param name="Observations">Reports assigned to this conflict in the window.</param>
/// <param name="Sources">
/// Distinct sources that produced them. The denominator. A count of events without this is a number
/// that cannot be interpreted, because it moves when the world changes and equally when the reporting
/// does.
/// </param>
/// <param name="Incidents">Distinct incidents those reports were correlated into.</param>
public sealed record ConflictWindowCounts(int Observations, int Sources, int Incidents)
{
    public static ConflictWindowCounts Empty { get; } = new(0, 0, 0);

    /// <summary>
    /// Reports per contributing source. The figure that separates "more happened" from "more people
    /// were watching", and the only one of the three that survives a change in who is reporting.
    /// </summary>
    public double PerSource => Sources == 0 ? 0 : Observations / (double)Sources;
}

/// <param name="Conflict">Register key.</param>
/// <param name="Name">The conflict as the coding names it.</param>
/// <param name="Current">This window.</param>
/// <param name="Previous">The window immediately before it, which is this conflict's own baseline.</param>
/// <param name="Verdict">What can be said about the change.</param>
/// <param name="Statement">The same thing in a sentence, for a reader who will not compute it.</param>
public sealed record ConflictTempo(
    string Conflict,
    string Name,
    ConflictWindowCounts Current,
    ConflictWindowCounts Previous,
    TempoVerdict Verdict,
    string Statement);

/// <summary>
/// Decides what one conflict's change in reporting volume is evidence of.
/// <para>
/// The reason this is not a subtraction: <b>rate of reporting is not rate of operations, and they
/// diverge exactly when it matters.</b> This repository already records the proof. The Tigray caveat
/// notes that ACLED's Ethiopia Peace Observatory ended fortnightly updates on 1 July 2025, roughly six
/// months before fighting resumed in January 2026. A line drawn across that boundary shows a
/// de-escalation at the moment of escalation — confidently, in the same typeface as a true one.
/// </para>
/// <para>
/// So every figure carries how many sources produced it, and a change is decomposed into "more events
/// reported" against "more sources reporting". When the two move together downwards, the honest
/// output is not a smaller number. It is <i>coverage changed; tempo cannot be stated</i>.
/// </para>
/// </summary>
public static class ConflictTempoAssessment
{
    /// <summary>
    /// Below this many reports in a window, a direction is noise. Three against one is a "two hundred
    /// per cent rise" and it is also two people writing about the same week.
    /// </summary>
    public const int MinimumObservations = 5;

    /// <summary>
    /// How much either figure must move before it is a change rather than jitter. A fifth, which is
    /// wide on purpose: a narrower threshold produces a page where everything is always moving, and
    /// a reader learns to ignore it.
    /// </summary>
    public const double MaterialChange = 0.2;

    public static ConflictTempo Assess(
        string key,
        string name,
        ConflictWindowCounts current,
        ConflictWindowCounts previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        if (previous.Observations == 0)
        {
            return new ConflictTempo(
                key,
                name,
                current,
                previous,
                TempoVerdict.NoBaseline,
                current.Observations == 0
                    ? "Nothing reached this system about this conflict in either window."
                    : $"{Reports(current.Observations)} this window, and none in the one before, so there is "
                      + "nothing to compare it against.");
        }

        if (current.Observations < MinimumObservations || previous.Observations < MinimumObservations)
        {
            return new ConflictTempo(
                key,
                name,
                current,
                previous,
                TempoVerdict.TooThin,
                $"{Reports(current.Observations)} this window against {previous.Observations} in the one "
                + "before. Too few either way for the direction to mean anything.");
        }

        var events = Change(current.Observations, previous.Observations);
        var sources = Change(current.Sources, previous.Sources);

        // Both fell. The case this exists for: fewer reports from fewer sources is exactly what a
        // conflict going quiet looks like and exactly what a conflict nobody is covering any more
        // looks like, and nothing in these numbers separates them.
        if (events <= -MaterialChange && sources <= -MaterialChange)
        {
            return new ConflictTempo(
                key,
                name,
                current,
                previous,
                TempoVerdict.NotStated,
                $"Reports fell from {previous.Observations} to {current.Observations}, and the sources "
                + $"producing them fell from {previous.Sources} to {current.Sources}. Coverage changed; "
                + "tempo cannot be stated.");
        }

        // Both rose. Some of the rise is the world and some of it is the number of people looking, and
        // reports per source is the part that survives the difference.
        if (events >= MaterialChange && sources >= MaterialChange)
        {
            var perSource = Change(current.PerSource, previous.PerSource);

            return perSource >= MaterialChange
                ? new ConflictTempo(
                    key,
                    name,
                    current,
                    previous,
                    TempoVerdict.Rising,
                    $"Reports rose from {previous.Observations} to {current.Observations}, from "
                    + $"{previous.Sources} sources to {current.Sources}. Each source is reporting more "
                    + "as well, so this is not only wider coverage.")
                : new ConflictTempo(
                    key,
                    name,
                    current,
                    previous,
                    TempoVerdict.NotStated,
                    $"Reports rose from {previous.Observations} to {current.Observations}, but so did the "
                    + $"sources, from {previous.Sources} to {current.Sources}, and each source is "
                    + "reporting about as much as before. This is more coverage, not evidently more "
                    + "fighting.");
        }

        // Sources steady. The comparison means what it appears to mean, which is the ordinary case and
        // the only one where a direction is stated plainly.
        if (Math.Abs(events) < MaterialChange)
        {
            return new ConflictTempo(
                key,
                name,
                current,
                previous,
                TempoVerdict.Steady,
                $"{Reports(current.Observations)} this window against {previous.Observations} before, "
                + $"from {current.Sources} sources against {previous.Sources}. No material change.");
        }

        var direction = events > 0 ? TempoVerdict.Rising : TempoVerdict.Falling;
        var moved = events > 0 ? "rose" : "fell";
        var basis = current.Sources == previous.Sources
            ? $"the same {previous.Sources} sources"
            : $"{previous.Sources} sources against {current.Sources}";

        return new ConflictTempo(
            key,
            name,
            current,
            previous,
            direction,
            $"Reports {moved} from {previous.Observations} to {current.Observations}, from {basis}. The "
            + "reporting base held, so the change is in what was reported rather than in who was "
            + "reporting it.");
    }

    private static double Change(double current, double previous) =>
        previous == 0 ? (current == 0 ? 0 : 1) : (current - previous) / previous;

    private static string Reports(int count) =>
        count == 1 ? "One report" : $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} reports";
}
