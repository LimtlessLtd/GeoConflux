namespace Geopolitics.Application.Control;

/// <summary>
/// The four numbers an assessment of control depends on.
/// <para>
/// Configurable rather than constant because [ADR 037] says staleness is the failure mode this
/// feature will actually have, and a horizon nobody can change is a horizon nobody can correct. The
/// defaults are reasoned below rather than picked, and none of them has been chosen against a
/// measurement yet — which is stated on the panel rather than left for a reader to assume.
/// </para>
/// </summary>
public sealed class ControlOptions
{
    public const string SectionName = "Control";

    /// <summary>
    /// How far back evidence is read at all. Ninety days because a transfer older than a season is
    /// describing a different war, and because reading further costs a table scan to produce
    /// assessments that would every one of them be stale.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// After this, an assessment reads as <em>last asserted</em> rather than as <em>held</em>.
    /// <para>
    /// Thirty days, and the reasoning is a comparison rather than a preference: ISW re-assesses
    /// daily. A pipeline that cannot must say how far behind it is rather than let an old assessment
    /// keep the appearance of a current one.
    /// </para>
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How close together two actors' claims have to be before the place is called contested rather
    /// than simply changed hands.
    /// <para>
    /// Fourteen days. A transfer to one actor in January and another in March is a place that changed
    /// hands, and the later one is the answer. Two transfers in a fortnight is a place being fought
    /// over, and picking the later one would present a snapshot of a moving thing as a settled fact.
    /// </para>
    /// </summary>
    public TimeSpan ContestWindow { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// How many independent sources an <em>uncoded</em> claim needs before it can assert anything.
    /// <para>
    /// Two, which is the corroboration gate's number and is here for the corroboration gate's reason:
    /// one channel saying a town has fallen is evidence that a channel said so. Coded evidence is
    /// exempt — a named organisation's coder working to published criteria has already done this.
    /// </para>
    /// </summary>
    public int MinimumClaimSources { get; set; } = 2;
}
