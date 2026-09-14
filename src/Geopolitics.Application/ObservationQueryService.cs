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
    /// <summary>
    /// Ceiling on one read. Raised from 200 on 2026-09-14, when the deploy went from four feeds to
    /// ten and a run began producing more observations than the snapshot exporter could ask for —
    /// so the published page carried a sample while every count beside it described the whole.
    /// <para>
    /// The endpoint defaults to 50 and a caller chooses from there, so this bounds the worst case
    /// rather than the usual one.
    /// </para>
    /// </summary>
    private const int MaxTake = 400;

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
