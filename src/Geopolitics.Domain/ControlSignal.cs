namespace Geopolitics.Domain;

/// <summary>
/// What a report says about who <em>holds</em> a place, as distinct from what happened there.
/// <para>
/// The distinction is the whole of [ADR 037]. Fighting at a place is evidence about that place and
/// is not a claim to hold it; an actor being coded as active somewhere says where fighting was
/// reported, which is a function of where journalists and coders were as much as where soldiers
/// were. So most observations carry <see cref="None"/>, and that is correct rather than a gap.
/// </para>
/// <para>
/// Deliberately small. Each member is a category somebody could check a report against, and a finer
/// taxonomy would demand a precision neither a coded field nor a classifier can deliver.
/// </para>
/// </summary>
public enum ControlSignal
{
    /// <summary>
    /// This report says nothing about control. The overwhelming majority, including nearly every
    /// report of fighting.
    /// </summary>
    None = 0,

    /// <summary>
    /// Control of a place passed to a named actor.
    /// <para>
    /// The strongest signal available, and the only one that can assert on its own authority — but
    /// only when it arrives coded. ACLED's <c>Government regains territory</c>,
    /// <c>Non-state actor overtakes territory</c> and <c>Non-violent transfer of territory</c> are a
    /// human coder asserting exactly this, with a date, a coordinate and a named party.
    /// </para>
    /// </summary>
    TerritoryTransferred,

    /// <summary>
    /// An actor established a base or headquarters at a place. Presence, not transfer: it says an
    /// actor is there and does not say the place changed hands.
    /// </summary>
    PresenceEstablished,

    /// <summary>
    /// An actor exercised administration — appointed officials, issued documents, restored
    /// utilities, ran a checkpoint. Weak individually and real in aggregate, and almost always
    /// reported in prose rather than in a coded field.
    /// </summary>
    AdministrationExercised,

    /// <summary>
    /// An actor left, withdrew, or was reported to have abandoned a place. Recorded because it is
    /// the only signal that can <em>end</em> an assessment without another actor asserting one, and
    /// a picture that could only ever gain control claims would drift permanently towards whoever
    /// was reported first.
    /// </summary>
    WithdrawalReported,
}

/// <summary>
/// Where a control signal came from, which is what decides how much weight it can carry.
/// </summary>
public enum ControlEvidenceBasis
{
    /// <summary>No control evidence.</summary>
    None = 0,

    /// <summary>
    /// A conflict-coding project's own field said so. A named organisation's coder already made this
    /// assessment against published criteria, so relaying it is aggregation rather than assessment,
    /// and it is the only basis that asserts alone.
    /// </summary>
    Coded,

    /// <summary>
    /// A source asserted it in prose. Governed by the corroboration gate exactly as a claim about an
    /// event is: one channel saying a town has fallen is evidence that a channel said so.
    /// </summary>
    Claimed,

    /// <summary>
    /// A model classified what kind of signal the prose carried, from an enumerated set, about a
    /// place the gazetteer had already resolved and an actor already in the register. It labels
    /// evidence; it never supplies a place, an actor, or a date, and it never decides that control
    /// changed.
    /// </summary>
    Model,
}
