using Geopolitics.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// Aggregate analytics queries against the relational store.
/// <para>
/// Every count here is a <c>GROUP BY</c> the database evaluates, returning one row per class rather
/// than one row per incident. That matters more than it looks: the naive alternative — load the
/// window and tally in memory — reads the same table five times over for five breakdowns, and its
/// cost grows with how busy the period was rather than with how many classes exist.
/// </para>
/// <para>
/// The one query that does return rows, <see cref="SampleIncidentsAsync"/>, projects four scalar
/// columns and takes a caller-supplied cap. It exists because timestamps are persisted as converted
/// tick values, and neither bucketing nor exponential decay can be expressed over them in SQL.
/// </para>
/// </summary>
public sealed class EfAnalyticsRepository(GeopoliticsDbContext dbContext) : IAnalyticsRepository
{
    public Task<int> CountIncidentsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        InWindow(windowStart, windowEnd).CountAsync(cancellationToken);

    public Task<int> CountCorrelatedIncidentsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        InWindow(windowStart, windowEnd).CountAsync(value => value.ObservationCount > 1, cancellationToken);

    public async Task<IReadOnlyList<CategoryCount>> CountIncidentsBySeverityAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        await InWindow(windowStart, windowEnd)
            .GroupBy(value => value.Severity)
            .Select(group => new CategoryCount(group.Key.ToString(), group.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CategoryCount>> CountIncidentsByEventTypeAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        await InWindow(windowStart, windowEnd)
            .GroupBy(value => value.EventType)
            .Select(group => new CategoryCount(group.Key.ToString(), group.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RegionCount>> CountIncidentsByRegionAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken)
    {
        // Grouped by country where the gazetteer resolved one and by place name where it did not.
        // Grouping on the country code alone would collapse every maritime incident on earth into a
        // single null bucket, and a regional breakdown whose largest row is "nowhere" is not a
        // regional breakdown.
        var grouped = await InWindow(windowStart, windowEnd)
            .Where(value => value.Location != null)
            .GroupBy(value => value.Location!.CountryCode ?? value.Location.Name)
            .Select(group => new
            {
                Code = group.Min(value => value.Location!.CountryCode),
                Name = group.Min(value => value.Location!.Name),
                Count = group.Count(),
            })
            .OrderByDescending(value => value.Count)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

        return [.. grouped.Select(value => new RegionCount(value.Code, value.Name ?? "Unnamed", value.Count))];
    }

    public async Task<IReadOnlyList<CategoryCount>> CountObservationsByKindAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        await ObservationsInWindow(windowStart, windowEnd)
            .GroupBy(value => value.Kind)
            .Select(group => new CategoryCount(group.Key.ToString(), group.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CategoryCount>> CountObservationsByStatusAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        await ObservationsInWindow(windowStart, windowEnd)
            .GroupBy(value => value.Status)
            .Select(group => new CategoryCount(group.Key.ToString(), group.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IncidentScoreSample> SampleIncidentsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(take, 1, 50_000);

        // One row over the cap, so truncation is detected by what came back rather than by comparing
        // against a second COUNT query that could disagree with this one under concurrent writes.
        var rows = await InWindow(windowStart, windowEnd)
            .OrderByDescending(value => value.OccurredAt)
            .Take(limit + 1)
            .Select(value => new IncidentScoreInput(
                value.Severity,
                value.OccurredAt,
                value.ObservationCount,
                value.ClassificationConfidence))
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > limit;

        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new IncidentScoreSample(rows, truncated);
    }

    /// <summary>
    /// Incidents in the half-open interval <c>[windowStart, windowEnd)</c>. Half-open so consecutive windows
    /// partition time exactly: an incident on a boundary belongs to one period, never to both.
    /// </summary>
    private IQueryable<Domain.GeopoliticalIncident> InWindow(DateTimeOffset windowStart, DateTimeOffset windowEnd) =>
        dbContext.Incidents
            .AsNoTracking()
            .Where(value => value.OccurredAt >= windowStart && value.OccurredAt < windowEnd);

    /// <summary>
    /// Observations by receipt time rather than event time. An observation's <c>OccurredAt</c> is
    /// optional and source-supplied, so counting ingestion by it would drop every report that omitted
    /// one and would let a source with a wrong clock move itself into another period.
    /// </summary>
    private IQueryable<Domain.RawObservation> ObservationsInWindow(DateTimeOffset windowStart, DateTimeOffset windowEnd) =>
        dbContext.Observations
            .AsNoTracking()
            .Where(value => value.ReceivedAt >= windowStart && value.ReceivedAt < windowEnd);
}
