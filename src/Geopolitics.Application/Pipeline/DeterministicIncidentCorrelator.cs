using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.Extensions.Options;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Decides whether an observation describes an event already tracked as an incident.
/// <para>
/// Every signal it uses can be recomputed from stored data and explained in a sentence: category,
/// time proximity, geographic distance, shared actors, and shared vocabulary. There is no model in
/// this path, which is what makes a correlation decision reproducible in a test and auditable
/// afterwards — the rationale recorded on each decision names the signals that actually fired.
/// </para>
/// <para>
/// The rule that shapes the whole class is that a wrong merge is worse than a missed one. Two
/// separate events folded into one incident destroy information and are close to invisible once
/// committed; two incidents that should have been one are obvious on the map and can be merged
/// later. Correlation therefore requires <em>positional</em> corroboration, never time and topic
/// alone, and the acceptance threshold is configurable rather than baked in.
/// </para>
/// </summary>
public sealed class DeterministicIncidentCorrelator(
    IIncidentRepository incidentRepository,
    ITextSimilarity textSimilarity,
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
            $"{candidates.Count} candidate(s) shared a category and time window, but none corroborated on location, actors, or wording.");
    }

    /// <summary>
    /// Returns a scored match, or <see langword="null"/> when the candidate is not the same event.
    /// <para>
    /// Confidence is a ceiling set by the positional evidence, pulled down by how weakly the other
    /// signals corroborate it. That shape matters. Measuring two reports 2 km apart establishes
    /// co-location; two reports that merely both say "Beirut" agree on a label covering a whole
    /// city, and should never be able to score as highly however well their wording happens to
    /// align. A plain weighted mean over the signals that were available cannot express that,
    /// because the missing distance signal simply drops out of the denominator and the weak match
    /// scores like the strong one.
    /// </para>
    /// </summary>
    private CorrelationAssessment? Score(RawObservation observation, GeopoliticalIncident candidate, DateTimeOffset occurredAt)
    {
        var hoursApart = Math.Abs((candidate.OccurredAt - occurredAt).TotalHours);
        var windowHours = options.CorrelationWindow.TotalHours;

        if (hoursApart > windowHours)
        {
            return null;
        }

        var reasons = new List<string>(4);
        var support = new List<Signal>(3)
        {
            new(1 - (hoursApart / windowHours), options.TimeWeight),
        };

        var entityOverlap = EntityOverlap(observation, candidate);

        if (entityOverlap is { } overlap)
        {
            support.Add(new(overlap.Score, options.EntityWeight));

            if (overlap.Shared > 0)
            {
                reasons.Add($"{overlap.Shared} shared actor(s)");
            }
        }

        var similarity = textSimilarity.Score(
            $"{observation.Title} {observation.Summary}",
            $"{candidate.Title} {candidate.Summary}");

        support.Add(new(similarity, options.SimilarityWeight));

        if (similarity >= options.SemanticSimilarityThreshold)
        {
            reasons.Add($"{similarity:P0} wording overlap");
        }

        if (PositionalCeiling(observation, candidate, similarity, entityOverlap) is not { } positional)
        {
            return null;
        }

        // Positional evidence leads the rationale because it is what actually establishes that the
        // two reports are about the same place; the rest explains how well that is corroborated.
        reasons.Insert(0, positional.Reason);

        var totalWeight = support.Sum(signal => signal.Weight);

        if (totalWeight <= 0)
        {
            return null;
        }

        var corroboration = support.Sum(signal => signal.Score * signal.Weight) / totalWeight;
        var confidence = Math.Round(
            positional.Ceiling * (options.SupportFloor + ((1 - options.SupportFloor) * corroboration)),
            3);

        if (confidence < options.MinimumCorrelationConfidence)
        {
            return null;
        }

        var rationale = $"Same category within {hoursApart:F1} h: {string.Join(", ", reasons)}.";
        return new CorrelationAssessment(candidate, confidence, rationale);
    }

    /// <summary>
    /// How strongly the two reports are established to be about the same place, which is the most
    /// confidence this match can reach. Returns <see langword="null"/> when nothing establishes it,
    /// in which case the candidate is not the same event as far as this correlator is concerned.
    /// <para>
    /// Requiring positional corroboration is the single rule that keeps this from over-merging. A
    /// wrong merge destroys the distinction between two real events and is close to invisible once
    /// committed, whereas two incidents that should have been one are obvious on the map. Time and
    /// topic alone would merge every protest reported on a busy day.
    /// </para>
    /// </summary>
    private Positional? PositionalCeiling(
        RawObservation observation,
        GeopoliticalIncident candidate,
        double similarity,
        EntitySignal? entityOverlap)
    {
        if (observation.Location is { } observed && candidate.Location is { } known)
        {
            var distance = observed.DistanceInKilometresTo(known);

            // A hard gate, not a low score. Beyond the radius the two reports describe different
            // places, and no amount of agreement in wording makes them the same event.
            if (distance > options.CorrelationRadiusKilometres)
            {
                return null;
            }

            // Decays from certainty at zero distance to a place-name match at the radius edge,
            // rather than to zero. Decaying to zero would quietly halve the configured radius: a
            // report near the boundary would score below the acceptance threshold no matter how
            // well everything else agreed, and the setting would no longer mean what it says.
            var ceiling = 1 - ((1 - options.PlaceNameConfidence) * (distance / options.CorrelationRadiusKilometres));
            return new Positional(ceiling, $"{distance:F1} km apart");
        }

        if (NamesMatch(observation.LocationName, candidate.Location?.Name))
        {
            return new Positional(options.PlaceNameConfidence, $"both placed at {observation.LocationName}");
        }

        // The escape hatch for reports that carry no usable location: two outlets naming the same
        // actors in near-identical words are describing one event, whether or not either of them
        // said where. Both conditions are required, because either alone is routinely true of
        // unrelated reports that merely share a category.
        if (similarity >= options.SemanticSimilarityThreshold && entityOverlap is { Shared: > 0 })
        {
            return new Positional(
                options.ContentOnlyConfidence,
                "matched on actors and wording, with no location on either report");
        }

        return null;
    }

    /// <summary>
    /// Proportion of the observation's named actors that this incident has already seen, or
    /// <see langword="null"/> when either side named nobody.
    /// <para>
    /// Null rather than zero, deliberately. A satellite detection names no actors, and scoring that
    /// as total disagreement would make thermal corroboration of a reported incident impossible.
    /// Absent evidence is not evidence of absence, so the signal is simply left out of the mean.
    /// </para>
    /// </summary>
    private static EntitySignal? EntityOverlap(RawObservation observation, GeopoliticalIncident candidate)
    {
        if (observation.Entities.Count == 0 || candidate.EntityKeys.Count == 0)
        {
            return null;
        }

        var known = candidate.EntityKeys.ToHashSet(StringComparer.Ordinal);
        var shared = observation.Entities.Count(entity => known.Contains(entity.MatchKey));

        // Denominated by the observation's own actors rather than by the union: the question is
        // whether this report is about people already involved, not whether it mentions everyone
        // the incident has ever involved. A late report naming one of twenty known actors is still
        // strong corroboration.
        return new EntitySignal(shared, Math.Round((double)shared / observation.Entities.Count, 4));
    }

    private static bool NamesMatch(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <param name="Score">0-1 strength of this signal.</param>
    /// <param name="Weight">How much it counts relative to the others that were available.</param>
    private readonly record struct Signal(double Score, double Weight);

    private readonly record struct EntitySignal(int Shared, double Score);

    /// <param name="Ceiling">The most confidence this match may reach, set by the positional evidence.</param>
    /// <param name="Reason">Plain-language statement of what established co-location.</param>
    private readonly record struct Positional(double Ceiling, string Reason);
}
