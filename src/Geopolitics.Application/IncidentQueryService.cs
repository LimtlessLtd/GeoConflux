using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;

namespace Geopolitics.Application;

public sealed class IncidentQueryService(IIncidentRepository incidentRepository) : IIncidentQueryService
{
    public async Task<IReadOnlyList<IncidentResponse>> ListAsync(IncidentSearch search, CancellationToken cancellationToken)
    {
        var incidents = await incidentRepository.ListAsync(search, cancellationToken);
        return incidents.Select(IncidentResponse.FromDomain).ToArray();
    }

    public async Task<IncidentResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var incident = await incidentRepository.GetByIdAsync(id, cancellationToken);
        return incident is null ? null : IncidentResponse.FromDomain(incident);
    }
}
