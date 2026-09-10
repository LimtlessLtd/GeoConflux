using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Geopolitics.Api.Realtime;

/// <summary>
/// Broadcasts committed state to connected dashboards. Every method sends to all clients rather than
/// to groups: the dashboard is a single shared operational view, and filtering is a client concern.
/// <para>
/// This type performs no persistence and takes no decisions. If it fails, the processor logs it and
/// continues, because the record of what happened is already in the database.
/// </para>
/// </summary>
public sealed class SignalRIncidentNotifier(IHubContext<IncidentHub> hubContext) : IIncidentNotifier
{
    public Task IncidentCreatedAsync(IncidentResponse incident, CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync(IncidentHub.Messages.IncidentCreated, incident, cancellationToken);

    public Task IncidentUpdatedAsync(IncidentResponse incident, CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync(IncidentHub.Messages.IncidentUpdated, incident, cancellationToken);

    public Task ObservationReceivedAsync(ObservationResponse observation, CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync(IncidentHub.Messages.ObservationReceived, observation, cancellationToken);
}
