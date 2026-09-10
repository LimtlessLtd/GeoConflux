using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// Realtime fan-out of committed state. The processing pipeline calls this only after a successful
/// save, and treats every call as best-effort: a notifier failure must never fail the pipeline or
/// roll back persisted work, and the pipeline must run correctly with zero connected clients.
/// </summary>
public interface IIncidentNotifier
{
    Task IncidentCreatedAsync(IncidentResponse incident, CancellationToken cancellationToken);

    Task IncidentUpdatedAsync(IncidentResponse incident, CancellationToken cancellationToken);

    Task ObservationReceivedAsync(ObservationResponse observation, CancellationToken cancellationToken);
}
