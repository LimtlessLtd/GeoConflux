using Geopolitics.Domain;

namespace Geopolitics.Application.Operations;

/// <summary>
/// What a host is allowed to delete from its own database.
/// <para>
/// This is a rule rather than a query, and it is written here rather than inside the query for one
/// reason: the boundary it draws is the one [ADR 023] exists to protect, and a boundary that lives
/// only inside a <c>Where</c> clause is a boundary nobody reviews. The clause references this; this
/// says what it means.
/// </para>
/// <para>
/// <b>Evidence is what a source gave us. Derived state is what this system concluded.</b> A failed
/// commit keeps the first and drops the second. Retention works the other way round and must not
/// cross the same line from the other side: an observation an incident rests on is the evidence
/// behind a published conclusion, and deleting it would leave an incident asserting something with
/// nothing behind it — which is precisely the artefact this repository refuses to publish.
/// </para>
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// The two kinds of observation that can be deleted, and nothing else.
    /// <para>
    /// A <b>duplicate</b> is a repeat delivery. It is kept so that a redelivery stays auditable
    /// rather than vanishing, and <c>MarkDuplicate</c> clears its incident link as part of recording
    /// it, so it is behind nothing by construction.
    /// </para>
    /// <para>
    /// A <b>failure</b> is an observation the pipeline could not finish processing. It is kept so
    /// somebody can work out why, and its incident was rolled back with the save that failed. Past
    /// the horizon nobody is going to diagnose it.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<ObservationStatus> Prunable =
    [
        ObservationStatus.Duplicate,
        ObservationStatus.Failed,
    ];

    /// <summary>
    /// Why a held claim is not on that list, even though it is unlinked and therefore behind no
    /// incident.
    /// <para>
    /// Because it is <em>shown</em>. A claim held for corroboration is stored, placed, drawn on the
    /// map and counted in the coverage panel's split of published reporting against user-generated
    /// claims — which is one of the figures this dashboard exists to publish. A duplicate and a
    /// failure are neither displayed nor counted there. Deleting a held claim would therefore change
    /// what the page says about its own composition, and it would do it silently.
    /// </para>
    /// <para>
    /// It is a narrowing of what the sprint specified — unlinked, failed and duplicate material —
    /// and it is recorded as one rather than made quietly.
    /// </para>
    /// </summary>
    public const string HeldClaimsExcluded =
        "A claim held for corroboration is unlinked but is not waste: it is drawn on the map and "
        + "counted in the coverage panel's tier split. Retention leaves it alone, so what this "
        + "system says about its own composition does not change under it.";

    /// <summary>
    /// What a deletion costs, stated where the policy is rather than in a log nobody reads.
    /// </summary>
    public const string Cost =
        "Pruning changes what this host can say about its own past. Counts of duplicates and "
        + "failures are historical figures like any other, and once they are gone the only record "
        + "that they existed is that something removed them.";
}
