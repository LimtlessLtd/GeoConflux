using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.Application.Spatial;

/// <summary>
/// Answers spatial questions by narrowing with an indexable rectangle and then measuring exactly.
/// <para>
/// Two stages, and both matter. The bounding box is what the database can actually use an index for,
/// so it does the elimination: a search around a chokepoint touches the rows in that rectangle rather
/// than every incident ever recorded. The great-circle distance is what decides membership, because
/// a rectangle is not a circle — its corners reach roughly 1.4 times the radius — so the rows the box
/// returns are filtered again in memory against the real distance.
/// </para>
/// <para>
/// Doing it this way rather than through a spatial extension is a deliberate, documented choice
/// (ADR 017), not an absence of one. The measured reason is that the native SpatiaLite library is not
/// available on the platform that builds and publishes this project, and a spatial query path that
/// cannot run where the project actually runs is not a spatial query capability.
/// </para>
/// </summary>
public sealed class SpatialQueryService(
    IIncidentRepository incidentRepository,
    IChokepointCatalogue chokepoints,
    TimeProvider timeProvider) : ISpatialQueryService
{
    /// <summary>
    /// Ceiling on rows pulled from one rectangle before exact distances are measured. The box can be
    /// much larger than the circle inside it, so this bounds the cost of a deliberately wide search
    /// rather than the size of any sensible answer.
    /// </summary>
    private const int MaxCandidates = 1_000;

    /// <summary>Upper bound on a single chokepoint's returned evidence, so one busy passage cannot dominate a payload.</summary>
    private const int MaxIncidentsPerChokepoint = 10;

    public string Method => "bounding-box prefilter, great-circle distance";

    public async Task<IReadOnlyList<NearbyIncident>> FindIncidentsNearAsync(
        double latitude,
        double longitude,
        double radiusKilometres,
        int take,
        CancellationToken cancellationToken)
    {
        var results = await SearchAsync(latitude, longitude, radiusKilometres, occurredAfter: null, cancellationToken);
        return [.. results.Take(Math.Clamp(take, 1, 500))];
    }

    public async Task<IReadOnlyList<ChokepointActivity>> AnalyseChokepointsAsync(
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow() - (window > TimeSpan.Zero ? window : TimeSpan.FromHours(24));
        var activity = new List<ChokepointActivity>(chokepoints.Chokepoints.Count);

        foreach (var chokepoint in chokepoints.Chokepoints)
        {
            var nearby = await SearchAsync(
                chokepoint.Latitude,
                chokepoint.Longitude,
                chokepoint.WatchRadiusKilometres,
                since,
                cancellationToken);

            activity.Add(new ChokepointActivity(
                chokepoint.Name,
                chokepoint.Description,
                chokepoint.Latitude,
                chokepoint.Longitude,
                chokepoint.WatchRadiusKilometres,
                nearby.Count,
                CountBySeverity(nearby),

                // Nearest first from SearchAsync, so the head is the closest.
                nearby.Count == 0 ? null : Math.Round(nearby[0].DistanceKilometres, 1),
                [.. nearby.Take(MaxIncidentsPerChokepoint)]));
        }

        // Busiest first, then closest, so a reader sees where activity is concentrated. Chokepoints
        // with nothing are still returned: "quiet" is an answer, and dropping them would make the
        // watch list silently change length between requests.
        return [.. activity
            .OrderByDescending(value => value.IncidentCount)
            .ThenBy(value => value.NearestIncidentKilometres ?? double.MaxValue)
            .ThenBy(value => value.Name, StringComparer.Ordinal)];
    }

    private async Task<List<NearbyIncident>> SearchAsync(
        double latitude,
        double longitude,
        double radiusKilometres,
        DateTimeOffset? occurredAfter,
        CancellationToken cancellationToken)
    {
        var box = GeoBoundingBox.FromRadius(latitude, longitude, radiusKilometres);
        var candidates = await incidentRepository.ListWithinAsync(box, occurredAfter, MaxCandidates, cancellationToken);
        var centre = new GeoLocation("search", null, latitude, longitude);
        var matches = new List<NearbyIncident>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (candidate.Location is not { } location)
            {
                continue;
            }

            var distance = centre.DistanceInKilometresTo(location);

            // The rectangle admitted this row; the circle decides whether it stays.
            if (distance <= radiusKilometres)
            {
                matches.Add(new NearbyIncident(IncidentResponse.FromDomain(candidate), Math.Round(distance, 2)));
            }
        }

        matches.Sort((left, right) => left.DistanceKilometres.CompareTo(right.DistanceKilometres));
        return matches;
    }

    private static IReadOnlyList<SeverityCount> CountBySeverity(List<NearbyIncident> incidents) =>
        [.. incidents
            .GroupBy(value => value.Incident.Severity)
            .Select(group => new SeverityCount(group.Key.ToString(), group.Count()))
            .OrderByDescending(value => Enum.Parse<Severity>(value.Severity))];
}
