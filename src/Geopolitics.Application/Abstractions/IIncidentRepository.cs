using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

public interface IIncidentRepository
{
    Task<GeopoliticalIncident?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeopoliticalIncident>> ListAsync(IncidentSearch search, CancellationToken cancellationToken);

    /// <summary>
    /// Incidents of the same type inside a time window, which is the deterministic pre-filter the
    /// correlator narrows further by distance. Kept as a repository concern so the database does the
    /// coarse work rather than the correlator loading every incident.
    /// </summary>
    Task<IReadOnlyList<GeopoliticalIncident>> ListCorrelationCandidatesAsync(
        EventType eventType,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// Incidents whose coordinates fall inside a rectangle, optionally within a time window.
    /// <para>
    /// The rectangle is the indexable approximation of a search circle, so this deliberately returns
    /// a superset of what the caller wants and leaves exact distance to the caller. A database can
    /// index latitude and longitude; it cannot index a great-circle distance, and computing one per
    /// row would mean a full scan.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<GeopoliticalIncident>> ListWithinAsync(
        GeoBoundingBox boundingBox,
        DateTimeOffset? occurredAfter,
        int take,
        CancellationToken cancellationToken);

    Task AddAsync(GeopoliticalIncident incident, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
