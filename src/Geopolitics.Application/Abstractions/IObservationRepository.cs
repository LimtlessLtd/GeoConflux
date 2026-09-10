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

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
