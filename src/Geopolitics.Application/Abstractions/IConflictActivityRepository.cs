namespace Geopolitics.Application.Abstractions;

/// <param name="ConflictKeys">Conflicts this report was assigned to. Empty for reports belonging to none.</param>
/// <param name="SourceName">Who reported it. The denominator every tempo figure is stated against.</param>
/// <param name="IncidentId">The incident it joined, when it joined one.</param>
/// <param name="CandidateCount">
/// Conflicts whose geography contained it but which nothing in it identified. Carried so the tally
/// can tell "nothing covers this" apart from "several things might and nothing said which", which
/// look identical in a count of unassigned reports and mean opposite things about the register.
/// </param>
public sealed record ConflictObservationSample(
    IReadOnlyList<string> ConflictKeys,
    string SourceName,
    Guid? IncidentId,
    int CandidateCount);

/// <param name="Observations">The projected reports in the window.</param>
/// <param name="Truncated">
/// Whether the window held more than the cap allowed. Reported rather than swallowed, for the same
/// reason the activity score reports it: a tempo computed over part of a window is a different
/// number, and a reader comparing it with the previous window is entitled to know that one of them
/// was cut short.
/// </param>
public sealed record ConflictActivitySample(
    IReadOnlyList<ConflictObservationSample> Observations,
    bool Truncated);

/// <summary>
/// The reports behind per-conflict tempo.
/// <para>
/// This one returns rows rather than counts, which is a deliberate exception to how every other read
/// model here works. Conflict membership is a list per report — one report belongs to two wars — so
/// there is no <c>GROUP BY</c> that produces the right answer without the database understanding that
/// list, and SQLite does not. Grouping therefore happens in the application, the projection is cut to
/// the three fields the tally needs, and the sample is capped with the truncation reported.
/// </para>
/// <para>
/// The alternative — a membership row per report per conflict — is the right shape and is not worth
/// its migration until the volume justifies it. This is recorded here so the trade is visible rather
/// than discovered.
/// </para>
/// </summary>
public interface IConflictActivityRepository
{
    /// <summary>
    /// Reports whose event time falls in the half-open interval <c>[windowStart, windowEnd)</c>.
    /// </summary>
    Task<ConflictActivitySample> SampleAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// The reports one conflict's summary would be written from, newest first.
    /// <para>
    /// Excerpts rather than payloads, in line with how this project treats collected material
    /// everywhere: enough to characterise a window without reproducing anybody's reporting.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Conflicts.ConflictEvidence>> EvidenceAsync(
        string conflictKey,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken);
}
