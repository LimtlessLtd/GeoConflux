using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.Extensions.Options;

namespace Geopolitics.Application.Pipeline;

/// <param name="Corroborator">The independent claim that supports this one, or <see langword="null"/> when none does.</param>
/// <param name="Rationale">Plain-language statement of what was and was not found, recorded either way.</param>
public sealed record CorroborationOutcome(RawObservation? Corroborator, string Rationale)
{
    public bool IsCorroborated => Corroborator is not null;

    public static CorroborationOutcome Held(string rationale) => new(null, rationale);
}

/// <summary>
/// Decides whether a user-generated claim has earned the right to assert that something happened.
/// <para>
/// A wire item comes from an organisation with an editorial process, a correction policy, and a
/// reputation it is unwilling to spend. A Telegram post comes from a handle. It may be a first-hand
/// account minutes old and better than anything a wire will publish that day; it may equally be an
/// anonymous claim, footage recycled from a different war, or deception produced by a party to the
/// conflict. Nothing about the item alone separates those, and contested information space is the
/// normal operating condition for these channels rather than an edge case.
/// </para>
/// <para>
/// The response is not to exclude them — excluding them means excluding the fastest and often the
/// only reporting from inside an event. It is that one claim is evidence a post exists, and two
/// independent ones are evidence of an event. A held claim is stored, placed, classified and shown;
/// what is withheld is only the incident.
/// </para>
/// <para>
/// Distinct from <see cref="CorrelationLock"/>, which is a mutex and shares nothing with this but a
/// syllable, and distinct from <see cref="DeterministicIncidentCorrelator"/>, which asks a different
/// and weaker question. The correlator asks whether two reports are probably the same event and is
/// tuned so that a missed merge beats a wrong one. This asks whether an event may be asserted at
/// all, where a wrong answer manufactures an incident out of two posts. It is therefore stricter
/// on purpose, and it answers with a boolean rather than a score, because "may this exist" is not a
/// question a confidence between 0 and 1 answers.
/// </para>
/// </summary>
public sealed class CorroborationGate(
    IObservationRepository observations,
    ITextSimilarity textSimilarity,
    IOptions<PipelineOptions> options)
{
    private readonly PipelineOptions options = options.Value;

    /// <summary>
    /// Looks for an independent claim already held that supports this one.
    /// <para>
    /// Only reached when the correlator found no incident to join. A claim that matches an existing
    /// incident is already corroborated by whatever opened it, and there is nothing for this to
    /// decide.
    /// </para>
    /// </summary>
    public async Task<CorroborationOutcome> AssessAsync(RawObservation claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (claim.Attribution.Identity is not { } identity)
        {
            // An unattributed claim — the open write path — has no identity for anything to be
            // independent of. It can still join an incident that exists on other grounds; it can
            // never be the second source that brings one into being, because "two anonymous
            // strangers agreed" is one unverifiable assertion repeated, not two sources.
            return CorroborationOutcome.Held("the claim names no channel, so nothing can be independent of it");
        }

        var held = await HeldPeersAsync(claim.EventType, Occurred(claim), cancellationToken);
        var considered = 0;

        foreach (var peer in held)
        {
            if (peer.Id == claim.Id || peer.Attribution.Identity is not { } peerIdentity)
            {
                continue;
            }

            if (string.Equals(peerIdentity, identity, StringComparison.OrdinalIgnoreCase))
            {
                // One channel posting twice is one source posting twice. This is the rule that stops
                // a single account manufacturing an incident by repeating itself, and it is why the
                // identity carries the platform: telegram/reuters is not mastodon/reuters.
                continue;
            }

            considered++;

            if (Supports(claim, peer) is { } reason)
            {
                return new CorroborationOutcome(
                    peer,
                    $"corroborated by an independent claim on {peerIdentity}: {reason}");
            }
        }

        return CorroborationOutcome.Held(
            considered == 0
                ? "no independent claim of this category was waiting inside the window"
                : $"{considered} independent claim(s) shared a category and window, but none agreed on place");
    }

    /// <summary>
    /// Held claims that this incident now accounts for.
    /// <para>
    /// The sequence this exists for is the ordinary one, not the exotic one: social breaks first and
    /// the wire follows. Without this a post held at 09:00 would still be held at 17:00 after three
    /// outlets had reported the same strike, which would make the gate a way of losing the fastest
    /// reporting rather than a way of qualifying it.
    /// </para>
    /// <para>
    /// Returns rather than links. Every other mutation in this pipeline happens in the processor,
    /// inside the lock and the single save, and a service that quietly wrote to two of the caller's
    /// aggregates would be the one place that did not.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RawObservation>> ReleasableByAsync(
        GeopoliticalIncident incident,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var held = await HeldPeersAsync(incident.EventType, incident.OccurredAt, cancellationToken);

        // No identity check here, and that is not an oversight. The incident exists on grounds this
        // claim had no part in establishing, so the claim is corroborated by it whether or not some
        // other post from the same channel is also attached.
        return [.. held.Where(claim => Supports(claim, incident.EventType, incident.OccurredAt, incident.Location, incident.Location?.Name, $"{incident.Title} {incident.Summary}", incident.EntityKeys) is not null)];
    }

    private async Task<IReadOnlyList<RawObservation>> HeldPeersAsync(
        EventType eventType,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var window = options.CorrelationWindow;

        return await observations.ListHeldClaimsAsync(
            eventType,
            occurredAt - window,
            occurredAt + window,
            options.MaxHeldClaimsPerSweep,
            cancellationToken);
    }

    private string? Supports(RawObservation claim, RawObservation peer) => Supports(
        claim,
        peer.EventType,
        Occurred(peer),
        peer.Location,
        peer.LocationName,
        $"{peer.Title} {peer.Summary}",
        [.. peer.Entities.Select(entity => entity.MatchKey)]);

    /// <summary>
    /// Whether the other record supports this claim, and in what words, or <see langword="null"/>
    /// when it does not.
    /// <para>
    /// Two branches rather than the correlator's weighted arithmetic, and the shape is the argument.
    /// Strong positional evidence — two records putting the same category of event at the same town
    /// within hours — corroborates by itself, because two parties independently choosing the same
    /// coordinates is not something agreement in wording adds to. Weaker positional evidence, where
    /// the two merely name the same place, needs the text or the actors to agree as well.
    /// </para>
    /// <para>
    /// Wording is never <em>required</em>, and that is the point of the first branch. The whole
    /// purpose of reading open social is to hear an event described in Russian, Ukrainian, Persian
    /// and Arabic by people who are not reading each other, and a lexical similarity measure scores
    /// two such accounts near zero. A rule that demanded shared vocabulary would have quietly
    /// restricted corroboration to claims written in the same language — which is the failure this
    /// whole tier exists to escape.
    /// </para>
    /// </summary>
    private string? Supports(
        RawObservation claim,
        EventType eventType,
        DateTimeOffset occurredAt,
        GeoLocation? location,
        string? locationName,
        string text,
        IReadOnlyCollection<string> entityKeys)
    {
        if (eventType != claim.EventType)
        {
            return null;
        }

        var hoursApart = Math.Abs((occurredAt - Occurred(claim)).TotalHours);

        if (hoursApart > options.CorrelationWindow.TotalHours)
        {
            return null;
        }

        if (claim.Location is { SupportsDistanceComparison: true } claimed
            && location is { SupportsDistanceComparison: true } other)
        {
            var distance = claimed.DistanceInKilometresTo(other);

            if (distance <= options.CorroborationRadiusKilometres)
            {
                return $"both placed within {distance:F1} km, {hoursApart:F1} h apart";
            }

            // Measured apart. Two records that each know where they are and disagree are not the
            // same event, and no agreement in wording rescues that — unlike the coarse cases below,
            // where the position simply is not precise enough to have ruled anything out.
            return null;
        }

        // A shared country is deliberately not enough, which is where this parts company with the
        // correlator. "Both somewhere in Yemen" would let two unrelated posts on a busy day assert
        // an event between them, and the gate's whole job is to be the thing that does not do that.
        // Such a claim is not stranded: it is still released the moment an incident it matches
        // exists, by the ordinary correlation path, which weighs country-level agreement properly
        // against everything else it knows.
        var sharedName = !string.IsNullOrWhiteSpace(claim.LocationName)
            && !string.IsNullOrWhiteSpace(locationName)
            && string.Equals(claim.LocationName.Trim(), locationName.Trim(), StringComparison.OrdinalIgnoreCase)
            && claim.Location?.Precision != LocationPrecision.Country
            && location?.Precision != LocationPrecision.Country;

        if (!sharedName)
        {
            return null;
        }

        var shared = claim.Entities.Count(entity => entityKeys.Contains(entity.MatchKey));

        if (shared > 0)
        {
            return $"both placed at {claim.LocationName}, naming {shared} actor(s) in common";
        }

        var similarity = textSimilarity.Score($"{claim.Title} {claim.Summary}", text);

        return similarity >= options.SemanticSimilarityThreshold
            ? $"both placed at {claim.LocationName}, with {similarity:P0} wording overlap"
            : null;
    }

    private static DateTimeOffset Occurred(RawObservation observation) =>
        observation.OccurredAt ?? observation.ReceivedAt;
}
