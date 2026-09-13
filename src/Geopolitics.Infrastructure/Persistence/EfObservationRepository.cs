using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class EfObservationRepository(GeopoliticsDbContext dbContext) : IObservationRepository
{
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

    /// <summary>
    /// Held claims inside a window, tracked so the caller can release them.
    /// <para>
    /// Deliberately not <c>AsNoTracking</c>. Every other read here feeds a projection; this one
    /// feeds an update that has to commit alongside the incident releasing it, and a detached
    /// entity would have to be re-loaded to do that.
    /// </para>
    /// <para>
    /// Ordered oldest first so the claim that has waited longest is considered first. A sweep that
    /// hits its cap then leaves the newest claims held, which is the right way round: they have the
    /// most time left for a second source to arrive.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RawObservation>> ListHeldClaimsAsync(
        EventType eventType,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.Observations
            .Where(value => value.Status == ObservationStatus.Uncorroborated
                && value.EventType == eventType
                && value.OccurredAt >= windowStart
                && value.OccurredAt <= windowEnd)
            .OrderBy(value => value.ReceivedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Stores the evidence from an attempt that failed, without the incident that attempt had
    /// staged.
    /// <para>
    /// This is a repository operation rather than an <c>Add</c> and a <c>SaveChanges</c> at the call
    /// site because a failed commit leaves every pending change still tracked. Saving again simply
    /// re-attempts the work that just failed, so a transient fault would commit an incident sourced
    /// entirely from an observation the caller is in the middle of marking as failed — a record on
    /// the dashboard that nothing in the system believes in.
    /// </para>
    /// <para>
    /// Incidents are detached specifically rather than the tracker being cleared wholesale. The
    /// enrichment audit rows this attempt staged are evidence too: they carry no foreign key,
    /// deliberately, so that the record of having called a model survives the observation it
    /// describes. Discarding them here would quietly undo that decision on the one path where it
    /// matters most.
    /// </para>
    /// </summary>
    public async Task RetainEvidenceAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        foreach (var entry in dbContext.ChangeTracker.Entries<GeopoliticalIncident>().ToArray())
        {
            entry.State = EntityState.Detached;
        }

        await dbContext.Observations.AddAsync(observation, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Translation of a lost deduplication race lives on the context, so it applies to every save
    // through it rather than only to the ones issued from this class.
    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
