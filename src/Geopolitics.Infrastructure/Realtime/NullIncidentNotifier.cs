using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;

namespace Geopolitics.Infrastructure.Realtime;

/// <summary>
/// Default notifier for hosts with no realtime layer, such as the worker process and most tests.
/// Its existence is the practical proof of ADR 008: the pipeline is complete without SignalR, and
/// realtime delivery is an optional projection of state that has already been committed.
/// </summary>
public sealed class NullIncidentNotifier : IIncidentNotifier
{
    public Task IncidentCreatedAsync(IncidentResponse incident, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task IncidentUpdatedAsync(IncidentResponse incident, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ObservationReceivedAsync(ObservationResponse observation, CancellationToken cancellationToken) => Task.CompletedTask;
}
