using Geopolitics.Domain;

namespace Geopolitics.Application.Contracts;

/// <summary>API and realtime projection of an observation, including its processing outcome.</summary>
public sealed record ObservationResponse(
    Guid Id,
    ObservationKind Kind,
    string SourceName,
    string? Title,
    string? Summary,
    EventType EventType,
    Severity Severity,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? OccurredAt,
    string? LocationName,
    LocationResponse? Location,
    ObservationStatus Status,
    string? FailureReason,
    string? LocationResolutionNote,
    Guid? IncidentId,
    Guid? DuplicateOfObservationId,
    bool IsDemo)
{
    public static ObservationResponse FromDomain(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new ObservationResponse(
            observation.Id,
            observation.Kind,
            observation.SourceName,
            observation.Title,
            observation.Summary,
            observation.EventType,
            observation.Severity,
            observation.ReceivedAt,
            observation.OccurredAt,
            observation.LocationName,
            observation.Location is null
                ? null
                : new LocationResponse(
                    observation.Location.Name,
                    observation.Location.CountryCode,
                    observation.Location.Latitude,
                    observation.Location.Longitude),
            observation.Status,
            observation.FailureReason,
            observation.LocationResolutionNote,
            observation.IncidentId,
            observation.DuplicateOfObservationId,
            observation.IsDemo);
    }
}
