using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class EfAiInferenceRepository(GeopoliticsDbContext dbContext) : IAiInferenceRepository
{
    public Task AddAsync(AiInference inference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inference);
        return dbContext.Inferences.AddAsync(inference, cancellationToken).AsTask();
    }

    public async Task<IReadOnlyList<AiInference>> ListByObservationAsync(Guid observationId, CancellationToken cancellationToken) =>
        await dbContext.Inferences
            .AsNoTracking()
            .Where(value => value.ObservationId == observationId)
            .OrderBy(value => value.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
