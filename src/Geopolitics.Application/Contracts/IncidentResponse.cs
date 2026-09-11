using Geopolitics.Domain;

namespace Geopolitics.Application.Contracts;

/// <param name="ClassificationConfidence">
/// 0-1 confidence in the category and severity, from the best-supported evidence linked to this
/// incident. Always present and always displayed, so no classification is ever shown as a bare fact.
/// </param>
/// <param name="ClassificationMethod">
/// What produced that assessment, such as <c>keyword</c>, <c>source-declared</c>, or
/// <c>ai:Ollama/llama3.2</c>. Lets a reader tell a heuristic from an inference.
/// </param>
public sealed record IncidentResponse(
    Guid Id,
    string Title,
    string Summary,
    EventType EventType,
    Severity Severity,
    DateTimeOffset OccurredAt,
    LocationResponse? Location,
    int ObservationCount,
    bool IsDemo,
    double ClassificationConfidence,
    string ClassificationMethod)
{
    public static IncidentResponse FromDomain(GeopoliticalIncident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        return new IncidentResponse(
            incident.Id,
            incident.Title,
            incident.Summary,
            incident.EventType,
            incident.Severity,
            incident.OccurredAt,
            incident.Location is null
                ? null
                : new LocationResponse(
                    incident.Location.Name,
                    incident.Location.CountryCode,
                    incident.Location.Latitude,
                    incident.Location.Longitude),
            incident.ObservationCount,
            incident.IsDemo,
            incident.ClassificationConfidence,
            incident.ClassificationMethod);
    }
}

public sealed record LocationResponse(string Name, string? CountryCode, double Latitude, double Longitude);
