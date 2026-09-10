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

    Task AddAsync(GeopoliticalIncident incident, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
