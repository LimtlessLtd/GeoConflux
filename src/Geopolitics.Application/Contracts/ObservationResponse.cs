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
    bool IsDemo,
    double ClassificationConfidence,
    string ClassificationMethod,
    string? DetectedLanguage,
    string? SeverityRationale,
    IReadOnlyList<EntityResponse> Entities)
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
            observation.IsDemo,
            observation.ClassificationConfidence,
            observation.ClassificationMethod,
            observation.DetectedLanguage,
            observation.SeverityRationale,
            [.. observation.Entities.Select(entity => new EntityResponse(entity.Name, entity.Type))]);
    }
}

/// <summary>A named actor the enrichment step reported. A claim about the text, not a verified fact.</summary>
public sealed record EntityResponse(string Name, EntityType Type);
