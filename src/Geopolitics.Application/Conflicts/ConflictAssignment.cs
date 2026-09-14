using Geopolitics.Domain;

namespace Geopolitics.Application.Conflicts;

/// <param name="ConflictKey">The register key, such as <c>ucdp:13243</c>.</param>
/// <param name="ConflictName">The conflict as the coding names it, carried so a caller never has to
/// look it up to display it.</param>
/// <param name="Basis">Why this report was held to belong there.</param>
public sealed record ConflictMembership(string ConflictKey, string ConflictName, ConflictMatchBasis Basis);

/// <summary>
/// Which conflicts one report belongs to, and — just as importantly — which ones it might belong to
/// but cannot be said to.
/// <para>
/// The two lists are separate because collapsing them is the mistake this design is built to avoid.
/// A report from a country with four coded conflicts is inside the geography of all four; saying so
/// is a fact, and asserting it belongs to all four is arithmetic wearing the costume of analysis.
/// Candidates are shown, counted, and left unassigned until something identifies which.
/// </para>
/// </summary>
/// <param name="Memberships">
/// Conflicts this report belongs to. Usually one; more than one is expected and correct, because a
/// Houthi strike on shipping belongs to the war in Yemen and to the wider confrontation at once.
/// Counts across conflicts therefore do not sum to the total, and anything displaying them has to
/// say so rather than let a reader add them up.
/// </param>
/// <param name="Candidates">
/// Conflicts whose geography contains this report but which nothing in it identifies. Not
/// memberships, and not nothing.
/// </param>
/// <param name="Note">
/// Why there are no memberships, when there are none. Null when the assignment succeeded, because an
/// explanation of a thing that did not happen is noise.
/// </param>
public sealed record ConflictAssignment(
    IReadOnlyList<ConflictMembership> Memberships,
    IReadOnlyList<ConflictMembership> Candidates,
    string? Note)
{
    /// <summary>
    /// How many conflicts one report may be assigned to. Multi-membership is real and this is a guard
    /// on it, not a denial of it: past a handful, the assignment has stopped saying anything about
    /// this report and started saying something about the register.
    /// </summary>
    public const int MaxMemberships = 8;

    /// <summary>
    /// How many entries either list carries. A report inside the geography of forty conflicts has
    /// said something about the register rather than about itself, and forty rows would bury that
    /// under its own evidence — the count that matters is stated in the note instead.
    /// </summary>
    public const int MaxListed = 8;

    public static ConflictAssignment Nothing(string note) => new([], [], note);

    /// <summary>Whether this report was placed in any conflict at all.</summary>
    public bool IsAssigned => Memberships.Count > 0;
}
