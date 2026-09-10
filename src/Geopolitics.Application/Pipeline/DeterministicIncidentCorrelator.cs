using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.Extensions.Options;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Correlates on signals that can be recomputed and audited: category, time proximity, and
/// geographic distance. Semantic similarity is deliberately absent here so that Sprint 2 behaviour
/// is fully reproducible; an embedding-based correlator composes with this rather than replacing it.
/// </summary>
public sealed class DeterministicIncidentCorrelator(
    IIncidentRepository incidentRepository,
    IOptions<PipelineOptions> options) : IIncidentCorrelator
{
    private readonly PipelineOptions options = options.Value;

    public async Task<CorrelationAssessment> CorrelateAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var occurredAt = observation.OccurredAt ?? observation.ReceivedAt;
        var window = options.CorrelationWindow;
        var candidates = await incidentRepository.ListCorrelationCandidatesAsync(
            observation.EventType,
            occurredAt - window,
            occurredAt + window,
            cancellationToken);

        if (candidates.Count == 0)
        {
            return CorrelationAssessment.NewIncident("No incident of this type inside the correlation window.");
        }

        CorrelationAssessment? best = null;

        foreach (var candidate in candidates)
        {
            var assessment = Score(observation, candidate, occurredAt);

            if (assessment is not null && (best is null || assessment.Confidence > best.Confidence))
            {
                best = assessment;
            }
        }

        return best ?? CorrelationAssessment.NewIncident(
            "Candidates shared a category and time window but none matched on location.");
    }

    /// <summary>
    /// Returns a scored match, or <see langword="null"/> when the candidate is not the same event.
    /// Both components are computed as decay curves so a near-miss scores lower than an exact hit
    /// instead of every accepted match looking equally certain.
    /// </summary>
    private CorrelationAssessment? Score(RawObservation observation, GeopoliticalIncident candidate, DateTimeOffset occurredAt)
    {
        var hoursApart = Math.Abs((candidate.OccurredAt - occurredAt).TotalHours);
        var windowHours = options.CorrelationWindow.TotalHours;

        if (hoursApart > windowHours)
        {
            return null;
        }

        var timeScore = 1 - (hoursApart / windowHours);

        if (observation.Location is not null && candidate.Location is not null)
        {
            var distance = observation.Location.DistanceInKilometresTo(candidate.Location);

            if (distance > options.CorrelationRadiusKilometres)
            {
                return null;
            }

            var distanceScore = 1 - (distance / options.CorrelationRadiusKilometres);
            var confidence = Math.Round((0.6 * distanceScore) + (0.4 * timeScore), 3);

            return new CorrelationAssessment(
                candidate,
                confidence,
                $"Same category within {distance:F1} km and {hoursApart:F1} h of an existing incident.");
        }

        // Without coordinates on both sides, fall back to the place name the sources claimed.
        // This is a weaker signal, so it is scored lower and reported as such.
        if (!NamesMatch(observation.LocationName, candidate.Location?.Name))
        {
            return null;
        }

        return new CorrelationAssessment(
            candidate,
            Math.Round(0.5 * timeScore, 3),
            $"Same category and place name within {hoursApart:F1} h, without corroborating coordinates.");
    }

    private static bool NamesMatch(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
