using Geopolitics.Application.Analytics;

namespace Geopolitics.Application.Abstractions;

/// <param name="Start">Inclusive start of the bucket.</param>
/// <param name="Count">Incidents whose <c>OccurredAt</c> falls inside it.</param>
/// <param name="Elevated">
/// How many of those were High or Critical. Carried alongside the total rather than as a separate
/// series so the chart can show volume and seriousness together — a quiet hour with one Critical
/// incident and a busy hour of routine traffic are different facts, and a bare count conflates them.
/// </param>
public sealed record TimeBucket(DateTimeOffset Start, int Count, int Elevated);

/// <param name="Name">Chokepoint name as the catalogue records it.</param>
/// <param name="IncidentCount">Incidents inside its watch radius during the window.</param>
/// <param name="NearestKilometres">Distance to the closest one, or <see langword="null"/> when there are none.</param>
public sealed record ChokepointSummary(string Name, int IncidentCount, double? NearestKilometres);

/// <param name="ChokepointsWatched">Size of the catalogue, so a reader knows the denominator.</param>
/// <param name="ChokepointsWithActivity">How many had at least one incident inside their radius.</param>
/// <param name="ProximityCount">
/// Incident-to-chokepoint proximities, <b>not</b> distinct incidents. Watch radii overlap, so a
/// report placed between two passages is counted by both. Named for what it measures rather than
/// presented as an incident total that would quietly overstate itself.
/// </param>
/// <param name="Busiest">The chokepoint with the most activity, or <see langword="null"/> when nothing was recorded near any of them.</param>
/// <param name="Chokepoints">Per-chokepoint counts, busiest first, without the incident payloads the spatial endpoint returns.</param>
public sealed record MaritimeSummary(
    int ChokepointsWatched,
    int ChokepointsWithActivity,
    int ProximityCount,
    ChokepointSummary? Busiest,
    IReadOnlyList<ChokepointSummary> Chokepoints);

/// <summary>
/// Everything the analytics view shows for one window, assembled in a single response.
/// </summary>
/// <param name="Window">The window token, echoed so a cached or exported payload identifies itself.</param>
/// <param name="From">Inclusive start of the period, derived from <paramref name="GeneratedAt"/>.</param>
/// <param name="To">Exclusive end, which is the moment the report was built.</param>
/// <param name="IncidentCount">Distinct incidents that occurred in the window.</param>
/// <param name="CorrelatedIncidentCount">How many of those drew on more than one piece of evidence.</param>
/// <param name="ObservationCount">Observations received in the window, including duplicates and failures.</param>
/// <param name="SatelliteObservationCount">Thermal/hotspot detections among them, the satellite activity figure.</param>
/// <param name="Timeseries">Incident counts per bucket, oldest first, with empty buckets present rather than omitted.</param>
/// <param name="Score">The heuristic activity score. Always accompanied by its own formula and caveat.</param>
/// <param name="Truncated">Whether the timeseries and score were computed over a capped sample of the window.</param>
public sealed record AnalyticsReport(
    string Window,
    string WindowLabel,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    int IncidentCount,
    int CorrelatedIncidentCount,
    int ObservationCount,
    int SatelliteObservationCount,
    IReadOnlyList<CategoryCount> BySeverity,
    IReadOnlyList<CategoryCount> ByEventType,
    IReadOnlyList<RegionCount> ByRegion,
    IReadOnlyList<CategoryCount> BySourceKind,
    IReadOnlyList<CategoryCount> ByObservationStatus,
    IReadOnlyList<TimeBucket> Timeseries,
    MaritimeSummary Maritime,
    ActivityScore Score,
    bool Truncated);

/// <summary>
/// Analytical views over stored incidents and observations.
/// <para>
/// One call per window returns everything that window's view needs. The alternative — an endpoint per
/// chart — would have the dashboard issue six round trips whose answers must agree with each other,
/// and they would not: each would resolve "now" independently, so a total and its own breakdown could
/// disagree about which incidents were inside the period.
/// </para>
/// </summary>
public interface IAnalyticsService
{
    Task<AnalyticsReport> BuildAsync(AnalyticsWindow window, CancellationToken cancellationToken);
}
