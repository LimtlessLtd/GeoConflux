using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

/// <param name="Incident">The incident, as the API serves it.</param>
/// <param name="DistanceKilometres">Great-circle distance from the search centre.</param>
public sealed record NearbyIncident(IncidentResponse Incident, double DistanceKilometres);

/// <param name="Name">Chokepoint name as it appears in the catalogue.</param>
/// <param name="WatchRadiusKilometres">The radius searched around it, which varies by chokepoint.</param>
/// <param name="IncidentCount">Incidents inside that radius within the requested window.</param>
/// <param name="SeverityCounts">How those incidents break down by severity, highest first.</param>
/// <param name="NearestIncidentKilometres">Distance to the closest one, or <see langword="null"/> when there are none.</param>
/// <param name="Incidents">The incidents themselves, nearest first, so the dashboard can link to evidence.</param>
public sealed record ChokepointActivity(
    string Name,
    string Description,
    double Latitude,
    double Longitude,
    double WatchRadiusKilometres,
    int IncidentCount,
    IReadOnlyList<SeverityCount> SeverityCounts,
    double? NearestIncidentKilometres,
    IReadOnlyList<NearbyIncident> Incidents);

/// <param name="Severity">Wire-format severity name.</param>
public sealed record SeverityCount(string Severity, int Count);

/// <summary>
/// Spatial questions the dashboard asks of stored incidents: what is near a point, and what is
/// happening around the maritime chokepoints this system watches.
/// <para>
/// An interface rather than a concrete service because the answer depends on what the deployment can
/// compute. The shipped implementation narrows candidates with an indexable bounding box and then
/// measures exact great-circle distances; a deployment with a spatial extension available could
/// answer the same questions in SQL without changing a caller. See ADR 017 for why the portable path
/// is the default.
/// </para>
/// </summary>
public interface ISpatialQueryService
{
    /// <summary>
    /// Describes how distances in these results were computed, so the dashboard can state it rather
    /// than implying a precision the backend does not have.
    /// </summary>
    string Method { get; }

    /// <summary>Incidents within <paramref name="radiusKilometres"/> of a point, nearest first.</summary>
    Task<IReadOnlyList<NearbyIncident>> FindIncidentsNearAsync(
        double latitude,
        double longitude,
        double radiusKilometres,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Activity around every catalogued maritime chokepoint within the given window, busiest first.
    /// </summary>
    /// <param name="window">How far back to look. Chokepoints with nothing in the window are still returned, with a count of zero.</param>
    Task<IReadOnlyList<ChokepointActivity>> AnalyseChokepointsAsync(
        TimeSpan window,
        CancellationToken cancellationToken);
}
