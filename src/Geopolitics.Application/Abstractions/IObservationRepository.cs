using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

public interface IObservationRepository
{
    /// <summary>
    /// Returns the identifier of an already-accepted observation carrying the same fingerprint,
    /// or <see langword="null"/> when this payload has not been seen before.
    /// </summary>
    Task<Guid?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken);

    Task AddAsync(RawObservation observation, CancellationToken cancellationToken);

    /// <summary>Most recently received observations, newest first, for the live feed.</summary>
    Task<IReadOnlyList<RawObservation>> ListRecentAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<RawObservation>> ListByIncidentAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>
    /// User-generated claims of one category that are waiting for a second source, inside a window.
    /// <para>
    /// Returned tracked rather than read-only, unlike every other query here, because these are the
    /// one set the caller exists to modify: a released claim is linked to an incident and saved in
    /// the same transaction that creates it. Reading them detached would mean re-loading each one to
    /// write it, inside the lock, for no benefit.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RawObservation>> ListHeldClaimsAsync(
        EventType eventType,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores this observation and the audit trail of the attempt, discarding the incident that
    /// attempt had staged.
    /// <para>
    /// Used on the recovery paths, where what was in flight has already failed. Those need to keep
    /// the source payload without also committing an incident assembled from an observation the
    /// pipeline went on to report as failed or duplicate — a record nothing in the system believes
    /// in, which would nonetheless reach the dashboard.
    /// </para>
    /// </summary>
    Task RetainEvidenceAsync(RawObservation observation, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
