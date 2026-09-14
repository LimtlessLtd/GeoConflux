namespace Geopolitics.Domain;

/// <summary>
/// Why an observation was held to belong to a conflict, ordered weakest to strongest.
/// <para>
/// Recorded rather than discarded because membership is an inference and the strength of it varies
/// enormously. "The source's own coding says so" and "it happened in a country this conflict is also
/// fought in" are both memberships, and presenting them identically would let the second borrow the
/// authority of the first. The order is meaningful: where several bases hold, the strongest is the
/// one recorded.
/// </para>
/// </summary>
public enum ConflictMatchBasis
{
    /// <summary>No basis. The observation does not belong to this conflict.</summary>
    None = 0,

    /// <summary>
    /// The observation was placed in a country this conflict has coded events in, and nothing
    /// narrower matched.
    /// <para>
    /// The weakest of the bases and deliberately not sufficient on its own, because most countries
    /// with one conflict have two. A report from Ethiopia is inside the geography of every conflict
    /// coded in Ethiopia, and asserting it belongs to all of them would be arithmetic rather than
    /// analysis. This is a <em>candidate</em>: something else — the source's coding, an actor, or a
    /// model — has to settle it.
    /// </para>
    /// </summary>
    Country,

    /// <summary>
    /// The observation was placed at a settlement or district this conflict has had coded events at.
    /// Much narrower than the country, and the strongest claim geography alone can make.
    /// </summary>
    Place,

    /// <summary>
    /// The report names a party to this conflict. What makes a conflict fought across many countries
    /// expressible at all: Israel/Iran is not a box on a map, and an actor is the only thing that
    /// travels with it into Syria, Lebanon, Iraq, Yemen and the Gulf.
    /// </summary>
    Actor,

    /// <summary>
    /// A model assigned the observation into this conflict.
    /// <para>
    /// Ranked above an actor match because the model has read the whole report rather than matched a
    /// string in it, and because the alternative bases are mechanical while this one is a judgement
    /// that carries its own recorded confidence. It is above geography and actors and below coding
    /// for the same reason: it is the best reading available of a report that states nothing, and it
    /// is still a reading.
    /// </para>
    /// </summary>
    Assigned,

    /// <summary>
    /// The source's own coding named this conflict. Authoritative, and the only basis that excludes
    /// the others: a UCDP record states which conflict it belongs to, and nothing this system infers
    /// improves on that.
    /// </summary>
    Coded,
}
