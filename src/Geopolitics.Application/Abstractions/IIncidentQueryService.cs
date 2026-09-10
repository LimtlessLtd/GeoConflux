using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

public interface IIncidentQueryService
{
    Task<IReadOnlyList<IncidentResponse>> ListAsync(IncidentSearch search, CancellationToken cancellationToken);

    Task<IncidentResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
