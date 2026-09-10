using Microsoft.AspNetCore.SignalR;

namespace Geopolitics.Api.Realtime;

/// <summary>
/// Realtime endpoint the dashboard connects to. It is deliberately empty of server-callable methods:
/// per ADR 008 this hub is an outbound projection of committed state, not an entry point into the
/// processing pipeline. Clients that miss messages recover by re-querying the REST API.
/// </summary>
public sealed class IncidentHub : Hub
{
    public const string Path = "/hubs/incidents";

    /// <summary>Message names the client subscribes to. Kept in one place so the contract is explicit.</summary>
    public static class Messages
    {
        public const string IncidentCreated = "incidentCreated";
        public const string IncidentUpdated = "incidentUpdated";
        public const string ObservationReceived = "observationReceived";
    }
}
