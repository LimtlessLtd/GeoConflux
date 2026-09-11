using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.Application.Analytics;

/// <summary>
/// Assembles one window's analytical view from aggregate queries, a capped incident sample, and the
/// existing chokepoint analysis.
/// <para>
/// The split between those three is the interesting decision. Counts by severity, type, region,
/// source kind, and status are exact and unbounded, because they are <c>GROUP BY</c> queries the
/// database answers without returning rows. The timeseries and the activity score are not: both need
/// per-incident time arithmetic, and timestamps are stored as converted tick values that SQLite
/// cannot bucket or decay in SQL. Those two are therefore computed from a capped projection of four
/// scalar columns, and the cap is reported in the response rather than hidden — a score over part of
/// a window is a different number, and the reader is told when they are looking at one.
/// </para>
/// </summary>
public sealed class AnalyticsService(
    IAnalyticsRepository analytics,
    ISpatialQueryService spatial,
    TimeProvider timeProvider) : IAnalyticsService
{
    /// <summary>
    /// Ceiling on incidents projected for the timeseries and the score. Well above anything the
    /// replay dataset or a single-node deployment produces in ninety days, and low enough that a
    /// runaway ingestion cannot turn one dashboard refresh into a full table scan into memory.
    /// </summary>
    public const int MaxSampledIncidents = 5_000;

    /// <summary>How many countries the regional breakdown returns before the tail is dropped.</summary>
    private const int MaxRegions = 12;

    public async Task<AnalyticsReport> BuildAsync(AnalyticsWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Resolved once and passed everywhere. Each query calling GetUtcNow for itself would give the
        // breakdowns slightly different periods, and a total that disagrees with the sum of its own
        // parts is worse than no total at all.
        var now = timeProvider.GetUtcNow();
        var from = now - window.Duration;

        var incidentCount = await analytics.CountIncidentsAsync(from, now, cancellationToken);
        var correlated = await analytics.CountCorrelatedIncidentsAsync(from, now, cancellationToken);
        var bySeverity = await analytics.CountIncidentsBySeverityAsync(from, now, cancellationToken);
        var byEventType = await analytics.CountIncidentsByEventTypeAsync(from, now, cancellationToken);
        var byRegion = await analytics.CountIncidentsByRegionAsync(from, now, MaxRegions, cancellationToken);
        var byKind = await analytics.CountObservationsByKindAsync(from, now, cancellationToken);
        var byStatus = await analytics.CountObservationsByStatusAsync(from, now, cancellationToken);
        var sample = await analytics.SampleIncidentsAsync(from, now, MaxSampledIncidents, cancellationToken);
        var chokepoints = await spatial.AnalyseChokepointsAsync(window.Duration, cancellationToken);

        var maritime = Summarise(chokepoints);
        var score = ActivityScoreCalculator.Calculate(sample, window, now);

        return new AnalyticsReport(
            window.Token,
            window.Label,
            from,
            now,
            now,
            incidentCount,
            correlated,
            byKind.Sum(value => value.Count),
            byKind.FirstOrDefault(value => value.Category == nameof(ObservationKind.Satellite))?.Count ?? 0,
            Order(bySeverity, value => (int)Enum.Parse<Severity>(value.Category)),
            [.. byEventType.OrderByDescending(value => value.Count).ThenBy(value => value.Category, StringComparer.Ordinal)],
            byRegion,
            [.. byKind.OrderByDescending(value => value.Count).ThenBy(value => value.Category, StringComparer.Ordinal)],
            [.. byStatus.OrderByDescending(value => value.Count).ThenBy(value => value.Category, StringComparer.Ordinal)],
            Bucket(sample, window, from, now),
            maritime,
            score,
            sample.Truncated);
    }

    /// <summary>
    /// Buckets the sampled incidents into a fixed-length series ending at the report time.
    /// <para>
    /// Every bucket is emitted, including empty ones. A chart drawn from only the non-empty buckets
    /// would compress the gaps and show a steady trickle as a continuous stream.
    /// </para>
    /// </summary>
    private static List<TimeBucket> Bucket(
        IncidentScoreSample sample,
        AnalyticsWindow window,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var buckets = window.BucketCount;
        var counts = new int[buckets];
        var elevated = new int[buckets];

        foreach (var incident in sample.Inputs)
        {
            if (incident.OccurredAt < from || incident.OccurredAt >= to)
            {
                continue;
            }

            // The filter above already confines this to [0, BucketCount), so the clamp guards only
            // against the division itself: TimeSpan division is floating point, and a timestamp a
            // fraction below the window end can round to exactly the bucket count and index one
            // past the array.
            var index = Math.Clamp(
                (int)((incident.OccurredAt - from) / window.BucketSize),
                0,
                buckets - 1);

            counts[index]++;

            if (incident.Severity is Severity.High or Severity.Critical)
            {
                elevated[index]++;
            }
        }

        var series = new List<TimeBucket>(buckets);

        for (var index = 0; index < buckets; index++)
        {
            series.Add(new TimeBucket(from + (window.BucketSize * index), counts[index], elevated[index]));
        }

        return series;
    }

    private static MaritimeSummary Summarise(IReadOnlyList<ChokepointActivity> activity)
    {
        var summaries = activity
            .Select(value => new ChokepointSummary(value.Name, value.IncidentCount, value.NearestIncidentKilometres))
            .OrderByDescending(value => value.IncidentCount)
            .ThenBy(value => value.NearestKilometres ?? double.MaxValue)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();

        var withActivity = summaries.Count(value => value.IncidentCount > 0);

        return new MaritimeSummary(
            summaries.Length,
            withActivity,
            summaries.Sum(value => value.IncidentCount),
            withActivity == 0 ? null : summaries[0],
            summaries);
    }

    /// <summary>
    /// Orders severity counts by rank rather than by frequency, so the row order is the same on every
    /// refresh and a reader can find Critical in the same place each time.
    /// </summary>
    private static IReadOnlyList<CategoryCount> Order(
        IReadOnlyList<CategoryCount> counts,
        Func<CategoryCount, int> rank) =>
        [.. counts.OrderByDescending(rank)];
}
