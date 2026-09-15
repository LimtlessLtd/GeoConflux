using Geopolitics.Domain;

namespace Geopolitics.Application.Contracts;

/// <summary>
/// API and realtime projection of an observation, including its processing outcome.
/// <para>
/// It carries both sides of the text on purpose. <see cref="Title"/> and
/// <see cref="OriginalContent"/> are the source's own words; <see cref="TranslatedTitle"/> and
/// <see cref="TranslatedSummary"/> are the English rendering, when one exists. A client that showed
/// only the English would leave a reader no way to check a translated claim, and one that showed
/// only the original would leave them unable to read half the feed. Which is displayed is the
/// reader's choice, so both have to arrive.
/// </para>
/// </summary>
public sealed record ObservationResponse(
    Guid Id,
    ObservationKind Kind,
    string SourceName,
    string? Title,
    string? Summary,
    EventType EventType,
    Severity Severity,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? OccurredAt,
    string? LocationName,
    LocationResponse? Location,
    ObservationStatus Status,
    string? FailureReason,
    string? LocationResolutionNote,
    Guid? IncidentId,
    Guid? DuplicateOfObservationId,
    bool IsDemo,
    ObservationProvenance Provenance,
    DateTimeOffset? CollectedAt,
    SourceTier Tier,
    string? Platform,
    string? Channel,
    double ClassificationConfidence,
    string ClassificationMethod,
    string? DetectedLanguage,
    string? OriginalContent,
    TranslationState Translation,
    string? TranslatedTitle,
    string? TranslatedSummary,
    string? TranslationMethod,
    string? SeverityRationale,
    IReadOnlyList<EntityResponse> Entities,
    SeverityOpinion? ModelSeverity,
    ConflictMembershipResponse Conflicts)
{
    /// <summary>
    /// How much source text is published. The stored column holds up to 20,000 characters, and four
    /// hundred observations of that length would be an eight-megabyte payload for a static page —
    /// most of it tail nobody reads.
    /// <para>
    /// Four thousand matches the bound normalisation already applies to a summary built from this
    /// same text, so the worst case here is no larger than the worst case that shipped before the
    /// original was exposed at all. Truncation is marked, because text that stops early and does not
    /// say so is the failure this whole change exists to correct.
    /// </para>
    /// </summary>
    public const int MaxOriginalContentLength = 4000;

    public static ObservationResponse FromDomain(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new ObservationResponse(
            observation.Id,
            observation.Kind,
            observation.SourceName,
            observation.Title,
            observation.Summary,
            observation.EventType,
            observation.Severity,
            observation.ReceivedAt,
            observation.OccurredAt,
            observation.LocationName,
            observation.Location is null
                ? null
                : new LocationResponse(
                    observation.Location.Name,
                    observation.Location.CountryCode,
                    observation.Location.Latitude,
                    observation.Location.Longitude,
                    observation.Location.Precision),
            observation.Status,
            observation.FailureReason,
            observation.LocationResolutionNote,
            observation.IncidentId,
            observation.DuplicateOfObservationId,
            observation.IsDemo,
            observation.Provenance,
            observation.CollectedAt,
            observation.Tier,
            observation.Platform,
            observation.Channel,
            observation.ClassificationConfidence,
            observation.ClassificationMethod,
            observation.DetectedLanguage,
            Excerpt(observation.Content),
            observation.Translation,
            observation.TranslatedTitle,
            observation.TranslatedSummary,
            observation.TranslationMethod,
            observation.SeverityRationale,
            [.. observation.Entities.Select(entity => new EntityResponse(entity.Name, entity.Type))],
            observation.ModelSeverity is { } predicted
                ? new SeverityOpinion(
                    predicted,
                    observation.ModelSeverityConfidence ?? 0,
                    observation.ModelVersion ?? "unknown",
                    observation.ModelDisagrees)
                : null,
            new ConflictMembershipResponse(
                observation.ConflictKeys,
                observation.ConflictBasis,
                observation.ConflictCandidateKeys,
                observation.ConflictNote));
    }

    private static string Excerpt(string content) =>
        content.Length <= MaxOriginalContentLength
            ? content
            : string.Concat(content.AsSpan(0, MaxOriginalContentLength), "… [truncated]");
}

/// <summary>A named actor the enrichment step reported. A claim about the text, not a verified fact.</summary>
public sealed record EntityResponse(string Name, EntityType Type);

/// <summary>
/// Which conflicts a report belongs to, which ones it might belong to, and why it belongs to none
/// when it does not.
/// <para>
/// One object rather than four loose fields, so nothing can read the memberships without also having
/// the candidates and the note in front of it. An empty list on its own reads as "nothing here",
/// which is exactly the wrong conclusion when the truth is that four wars overlap where this report
/// was placed and no coding says which one it is.
/// </para>
/// </summary>
/// <param name="Keys">Register keys this report belongs to. More than one is expected, so counts across conflicts do not sum.</param>
/// <param name="Basis">What the membership rests on, from the source coding it to it merely happening in the right country.</param>
/// <param name="Candidates">Conflicts whose geography contains it but which nothing in it identifies.</param>
/// <param name="Note">Why there is no membership, when there is none.</param>
public sealed record ConflictMembershipResponse(
    IReadOnlyList<string> Keys,
    ConflictMatchBasis? Basis,
    IReadOnlyList<string> Candidates,
    string? Note);

/// <summary>
/// What the trained severity model would have said about this observation.
/// <para>
/// A separate object rather than four loose fields, so a consumer has to acknowledge that this is a
/// second opinion before reading its value — and so <see langword="null"/> unambiguously means "no
/// prediction was made" rather than "predicted Unknown".
/// </para>
/// </summary>
/// <param name="Severity">The class the model predicted.</param>
/// <param name="Confidence">Its probability for that class. A model score against its training distribution, not a likelihood about the world.</param>
/// <param name="ModelVersion">Trainer, feature-set version, and dataset version.</param>
/// <param name="DisagreesWithApplied">Whether this differs from the severity the pipeline actually acted on, which is the case worth surfacing.</param>
public sealed record SeverityOpinion(
    Severity Severity,
    double Confidence,
    string ModelVersion,
    bool DisagreesWithApplied);
