using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.Application;

public sealed class IncidentQueryService(IIncidentRepository incidentRepository) : IIncidentQueryService
{
    public async Task<IReadOnlyList<IncidentResponse>> ListAsync(IncidentSearch search, CancellationToken cancellationToken)
    {
        var incidents = await incidentRepository.ListAsync(search, cancellationToken);
        return incidents.Select(ToResponse).ToArray();
    }

    public async Task<IncidentResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var incident = await incidentRepository.GetByIdAsync(id, cancellationToken);
        return incident is null ? null : ToResponse(incident);
    }

    private static IncidentResponse ToResponse(GeopoliticalIncident incident) => new(
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
        incident.IsDemo);
}
