using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class EfObservationRepository(GeopoliticsDbContext dbContext) : IObservationRepository
{
    /// <summary>SQLite error code raised when a unique index is violated.</summary>
    private const int SqliteConstraintUnique = 2067;

    public async Task<Guid?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken)
    {
        // Only observations that were actually accepted can be the original of a duplicate; matching
        // against previously-rejected duplicates would chain them into a misleading lineage.
        var match = await dbContext.Observations
            .AsNoTracking()
            .Where(value => value.Fingerprint == fingerprint && value.Status != ObservationStatus.Duplicate)
            .Select(value => (Guid?)value.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return match;
    }

    public Task AddAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return dbContext.Observations.AddAsync(observation, cancellationToken).AsTask();
    }

    public async Task<IReadOnlyList<RawObservation>> ListRecentAsync(int take, CancellationToken cancellationToken) =>
        await dbContext.Observations
            .AsNoTracking()
            .OrderByDescending(value => value.ReceivedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RawObservation>> ListByIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
        await dbContext.Observations
            .AsNoTracking()
            .Where(value => value.IncidentId == incidentId)
            .OrderByDescending(value => value.ReceivedAt)
            .ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsFingerprintConflict(exception))
        {
            // The read-then-insert duplicate check is not atomic across concurrent processors, so
            // the unique index is the real authority. Translate to a domain-level signal rather than
            // leaking a provider exception into the Application layer.
            throw new DuplicateObservationException(FingerprintOf(exception), exception);
        }
    }

    private static bool IsFingerprintConflict(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique } inner
        && inner.Message.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase);

    private static string FingerprintOf(DbUpdateException exception) =>
        exception.Entries
            .Select(entry => entry.Entity)
            .OfType<RawObservation>()
            .Select(observation => observation.Fingerprint)
            .FirstOrDefault() ?? "unknown";
}
