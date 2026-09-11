using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

public sealed class EfIncidentRepository(GeopoliticsDbContext dbContext) : IIncidentRepository
{
    /// <summary>
    /// Upper bound on candidates scored per observation. A busy category in a wide window could
    /// otherwise pull thousands of rows into memory for one insert; the newest incidents are the
    /// plausible matches, so ordering by recency and capping loses nothing in practice.
    /// </summary>
    private const int MaxCorrelationCandidates = 200;

    public Task<GeopoliticalIncident?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Incidents.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    public async Task<IReadOnlyList<GeopoliticalIncident>> ListAsync(IncidentSearch search, CancellationToken cancellationToken)
    {
        var query = dbContext.Incidents.AsNoTracking().AsQueryable();

        if (search.From is not null)
        {
            query = query.Where(value => value.OccurredAt >= search.From);
        }

        if (search.To is not null)
        {
            query = query.Where(value => value.OccurredAt <= search.To);
        }

        return await query
            .OrderByDescending(value => value.OccurredAt)
            .Take(search.BoundedTake)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GeopoliticalIncident>> ListCorrelationCandidatesAsync(
        EventType eventType,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>

        // Tracked deliberately: the correlator hands the winning candidate back to the processor,
        // which mutates it and saves in the same unit of work as the new observation.
        await dbContext.Incidents
            .Where(value => value.EventType == eventType
                && value.OccurredAt >= windowStart
                && value.OccurredAt <= windowEnd)
            .OrderByDescending(value => value.OccurredAt)
            .Take(MaxCorrelationCandidates)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GeopoliticalIncident>> ListWithinAsync(
        GeoBoundingBox boundingBox,
        DateTimeOffset? occurredAfter,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundingBox);

        // Location is an owned type, so these compare against the incident's own latitude and
        // longitude columns and the composite index on them is usable.
        var query = dbContext.Incidents
            .AsNoTracking()
            .Where(value => value.Location != null
                && value.Location.Latitude >= boundingBox.South
                && value.Location.Latitude <= boundingBox.North);

        // A box spanning the 180th meridian is two longitude intervals, not one. Expressed as a
        // single BETWEEN it would select everything except the region actually wanted.
        query = boundingBox.CrossesAntimeridian
            ? query.Where(value => value.Location!.Longitude >= boundingBox.West
                || value.Location.Longitude <= boundingBox.East)
            : query.Where(value => value.Location!.Longitude >= boundingBox.West
                && value.Location.Longitude <= boundingBox.East);

        if (occurredAfter is { } since)
        {
            query = query.Where(value => value.OccurredAt >= since);
        }

        return await query
            .OrderByDescending(value => value.OccurredAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task AddAsync(GeopoliticalIncident incident, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return dbContext.Incidents.AddAsync(incident, cancellationToken).AsTask();
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
