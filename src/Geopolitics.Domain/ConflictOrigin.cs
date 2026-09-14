namespace Geopolitics.Domain;

/// <summary>
/// Where a conflict in the register came from, which decides what it is allowed to do.
/// <para>
/// The distinction is the whole reason this enum exists. A register a model wrote would be a
/// function of the model's exposure rather than of the world, and exposure tracks volume of
/// reporting — so the conflicts most likely to be missing are the under-reported ones, and an absent
/// category looks exactly like peace. The register is therefore taken from projects whose entire job
/// is cataloguing organised violence, and a model's contribution is recorded as a claim beside it.
/// </para>
/// </summary>
public enum ConflictOrigin
{
    /// <summary>
    /// Named by a conflict-coding project. UCDP assigns every event it codes to a named conflict
    /// with named parties on published criteria; ACLED codes actors and disorder types. Neither was
    /// compiled with any interest in what this system can see, which is what makes the register
    /// worth having.
    /// </summary>
    Coded = 0,

    /// <summary>
    /// Proposed by a model because nothing codes it yet.
    /// <para>
    /// Real and worth storing — coding projects publish on a cycle and a new conflict exists before
    /// it is catalogued — but it enters as a claim rather than as a category. It is displayed,
    /// labelled, and unable to restructure anything until a second source supports it, which is
    /// structurally identical to how a single social post is treated.
    /// </para>
    /// </summary>
    ModelProposed,
}
