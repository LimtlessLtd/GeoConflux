using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;

namespace Geopolitics.Application;

public interface IObservationQueryService
{
    Task<IReadOnlyList<ObservationResponse>> ListRecentAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ObservationResponse>> ListByIncidentAsync(Guid incidentId, CancellationToken cancellationToken);
}

/// <summary>
/// Read model for the live feed and the evidence list in the incident drawer. Kept separate from the
/// pipeline so that a slow or failing query path cannot interfere with ingestion.
/// </summary>
public sealed class ObservationQueryService(IObservationRepository observationRepository) : IObservationQueryService
{
    private const int MaxTake = 200;

    public async Task<IReadOnlyList<ObservationResponse>> ListRecentAsync(int take, CancellationToken cancellationToken)
    {
        var observations = await observationRepository.ListRecentAsync(Math.Clamp(take, 1, MaxTake), cancellationToken);
        return observations.Select(ObservationResponse.FromDomain).ToArray();
    }

    public async Task<IReadOnlyList<ObservationResponse>> ListByIncidentAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var observations = await observationRepository.ListByIncidentAsync(incidentId, cancellationToken);
        return observations.Select(ObservationResponse.FromDomain).ToArray();
    }
}
