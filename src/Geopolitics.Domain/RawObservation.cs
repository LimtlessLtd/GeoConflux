namespace Geopolitics.Domain;

/// <summary>
/// A single report received from one source. An observation is retained for provenance even
/// when it is a duplicate, fails enrichment, or cannot be located, so that evidence is never
/// destroyed by a downstream failure.
/// </summary>
public sealed class RawObservation
{
    /// <summary>
    /// A cap, not a target. Entity extraction runs over untrusted text and a model that misbehaves
    /// can return an arbitrarily long list; bounding it here keeps one bad response from bloating
    /// a row and the payloads derived from it.
    /// </summary>
    public const int MaxEntities = 12;

    private readonly List<ExtractedEntity> entities = [];

    private RawObservation()
    {
        SourceName = string.Empty;
        Content = string.Empty;
        Fingerprint = string.Empty;
    }

    public RawObservation(
        Guid id,
        ObservationKind kind,
        string sourceName,
        string content,
        string? sourceIdentifier,
        DateTimeOffset receivedAt,
        ObservationProvenance provenance,
        DateTimeOffset? collectedAt = null)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("An observation identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            throw new DomainException("An observation source is required.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainException("Observation content is required.");
        }

        Id = id;
        Kind = kind;
        SourceName = sourceName.Trim();
        Content = content.Trim();
        SourceIdentifier = string.IsNullOrWhiteSpace(sourceIdentifier) ? null : sourceIdentifier.Trim();
        ReceivedAt = receivedAt;
        Provenance = provenance;
        CollectedAt = collectedAt;
        Status = ObservationStatus.Received;
        Fingerprint = ObservationFingerprint.Compute(SourceName, SourceIdentifier, Content);
    }

    public Guid Id { get; private set; }

    public ObservationKind Kind { get; private set; }

    public string SourceName { get; private set; }

    public string Content { get; private set; }

    public string? SourceIdentifier { get; private set; }

    /// <summary>Deterministic identity used to reject exact re-deliveries of the same report.</summary>
    public string Fingerprint { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Which of the three intake paths this arrived by.</summary>
    public ObservationProvenance Provenance { get; private set; }

    /// <summary>
    /// When the collection run that found this happened. Null for anything not collected.
    /// <para>
    /// A third distinct time, and none of the other two will do. <see cref="ReceivedAt"/> is when
    /// this process read the bundle, which may be weeks later and says nothing about the reporting;
    /// <see cref="OccurredAt"/> is when the event happened. Only this answers "how current is what I
    /// am looking at", which is the question a reader of a collected record actually has.
    /// </para>
    /// </summary>
    public DateTimeOffset? CollectedAt { get; private set; }

    /// <summary>
    /// True only for the recorded replay stream. Derived rather than stored, so it cannot drift out
    /// of agreement with <see cref="Provenance"/> — which is exactly what two columns would do.
    /// </summary>
    public bool IsDemo => Provenance == ObservationProvenance.Recorded;

    public ObservationStatus Status { get; private set; }

    /// <summary>Why processing failed, when it did. Distinct from a merely unresolved location.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// Why coordinates are absent. An observation can be fully processed and still be unplaced, so
    /// this is kept separate from <see cref="FailureReason"/>: not knowing where something happened
    /// is a normal outcome, not a processing failure.
    /// </summary>
    public string? LocationResolutionNote { get; private set; }

    /// <summary>Short headline derived during normalisation.</summary>
    public string? Title { get; private set; }

    /// <summary>Human-readable summary derived during normalisation.</summary>
    public string? Summary { get; private set; }

    public EventType EventType { get; private set; } = EventType.Other;

    public Severity Severity { get; private set; } = Severity.Unknown;

    /// <summary>When the reported event happened, as opposed to when the report arrived.</summary>
    public DateTimeOffset? OccurredAt { get; private set; }

    /// <summary>Place name claimed by the source, before deterministic coordinate resolution.</summary>
    public string? LocationName { get; private set; }

    public GeoLocation? Location { get; private set; }

    /// <summary>The incident this observation was correlated with, once assessed.</summary>
    public Guid? IncidentId { get; private set; }

    /// <summary>Set when this delivery repeats an observation the pipeline already accepted.</summary>
    public Guid? DuplicateOfObservationId { get; private set; }

    /// <summary>
    /// How certain the classification is, on a 0-1 scale. Always populated, whether the category
    /// came from a language model or from the deterministic keyword fallback, because the dashboard
    /// must never present a category without saying how much to trust it.
    /// </summary>
    public double ClassificationConfidence { get; private set; }

    /// <summary>
    /// What produced the classification, for example <c>keyword</c>, <c>source-declared</c>, or
    /// <c>ai:ollama/llama3.2</c>. Stored so a reader can tell an inference from a heuristic.
    /// </summary>
    public string ClassificationMethod { get; private set; } = "none";

    /// <summary>
    /// BCP-47 language tag of the original payload, when enrichment identified one. Present so a
    /// translated summary can be shown alongside the language it was translated from.
    /// </summary>
    public string? DetectedLanguage { get; private set; }

    /// <summary>
    /// One or two sentences explaining the severity assessment. Bounded on purpose: this is a
    /// concise, structured justification for application use, not a reasoning transcript.
    /// </summary>
    public string? SeverityRationale { get; private set; }

    /// <summary>Named actors the enrichment step found in the payload. Empty when none were found.</summary>
    public IReadOnlyList<ExtractedEntity> Entities => entities.AsReadOnly();

    /// <summary>
    /// What the trained severity model thought, or <see langword="null"/> when it was disabled,
    /// unavailable, or not reached.
    /// <para>
    /// Recorded next to <see cref="Severity"/> rather than replacing it. The stored severity is the
    /// one the pipeline acted on; this is a second opinion, kept so the two can be compared after the
    /// fact on real traffic rather than only on a labelled corpus. Where they disagree, that
    /// disagreement is the interesting record.
    /// </para>
    /// </summary>
    public Severity? ModelSeverity { get; private set; }

    /// <summary>The model's probability for that class, 0-1. Null whenever <see cref="ModelSeverity"/> is.</summary>
    public double? ModelSeverityConfidence { get; private set; }

    /// <summary>
    /// Which model produced it — trainer, feature-set version, and dataset version. Persisted so a
    /// stored prediction can be attributed to an exact model rather than to "the severity model",
    /// which will mean something different in six months.
    /// </summary>
    public string? ModelVersion { get; private set; }

    /// <summary>
    /// Records the structured interpretation of the payload. Coordinates are supplied by a
    /// deterministic resolver and are never inferred from free text by a language model.
    /// </summary>
    public void ApplyNormalisation(
        string title,
        string summary,
        EventType eventType,
        Severity severity,
        DateTimeOffset occurredAt,
        string? locationName)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("A normalised observation requires a title.");
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainException("A normalised observation requires a summary.");
        }

        Title = title.Trim();
        Summary = summary.Trim();
        EventType = eventType;
        Severity = severity;
        OccurredAt = occurredAt;
        LocationName = string.IsNullOrWhiteSpace(locationName) ? null : locationName.Trim();
        Status = ObservationStatus.Normalised;
        FailureReason = null;
    }

    /// <summary>
    /// Records how much the current classification is worth and where it came from. Called for every
    /// observation, including those classified by the deterministic fallback, so that confidence is
    /// never absent from what the dashboard displays.
    /// </summary>
    public void ApplyClassificationProvenance(double confidence, string method)
    {
        if (confidence is < 0 or > 1)
        {
            throw new DomainException("Classification confidence must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new DomainException("A classification must record the method that produced it.");
        }

        ClassificationConfidence = confidence;
        ClassificationMethod = method.Trim();
    }

    /// <summary>
    /// Applies a validated enrichment result over the normalised values.
    /// <para>
    /// Only called once the payload has passed schema validation, which is why this method trusts
    /// its arguments' shape while still enforcing domain invariants. Two rules hold regardless of
    /// what the model returned: a source-declared category is not overwritten by an inferred one
    /// (handled by the caller), and no coordinate ever arrives through here — a place <em>name</em>
    /// may be proposed, and a deterministic resolver alone turns it into a position.
    /// </para>
    /// </summary>
    public void ApplyEnrichment(
        string summary,
        EventType eventType,
        Severity severity,
        double confidence,
        string method,
        string? detectedLanguage,
        string? severityRationale,
        string? locationName,
        IEnumerable<ExtractedEntity> extractedEntities)
    {
        ArgumentNullException.ThrowIfNull(extractedEntities);

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainException("An enriched observation requires a summary.");
        }

        Summary = summary.Trim();
        EventType = eventType;
        Severity = severity;
        DetectedLanguage = string.IsNullOrWhiteSpace(detectedLanguage) ? null : detectedLanguage.Trim();
        SeverityRationale = Cap(severityRationale, AiInference.MaxRationaleLength);

        // A proposed place name only fills a gap. A name the source stated itself is a fact about
        // the record; a name inferred from prose is a reading of it, and the stronger claim wins.
        if (string.IsNullOrWhiteSpace(LocationName) && !string.IsNullOrWhiteSpace(locationName))
        {
            LocationName = locationName.Trim();
        }

        entities.Clear();

        foreach (var entity in extractedEntities.Take(MaxEntities))
        {
            if (entity is not null && !entities.Any(existing => existing.MatchKey == entity.MatchKey))
            {
                entities.Add(entity);
            }
        }

        ApplyClassificationProvenance(confidence, method);
        Status = ObservationStatus.Enriched;
        FailureReason = null;
    }

    /// <summary>
    /// Applies only the factual extractions from an enrichment result, leaving the classification
    /// alone.
    /// <para>
    /// Used when a model answered but reported low confidence. Confidence describes certainty in the
    /// <em>classification</em>, so a model unsure whether a report is piracy or a maritime incident
    /// may still be entirely right that the text is Arabic and names Bab-el-Mandeb. Discarding those
    /// because a different field was uncertain throws away good data; the category, severity, and
    /// summary stay deterministic, which is what the low confidence actually justified.
    /// </para>
    /// <para>
    /// Nothing adopted here can place the observation: a location <em>name</em> still has to survive
    /// the deterministic resolver before it becomes a position.
    /// </para>
    /// </summary>
    public void ApplyExtractions(
        string? detectedLanguage,
        string? locationName,
        IEnumerable<ExtractedEntity> extractedEntities)
    {
        ArgumentNullException.ThrowIfNull(extractedEntities);

        DetectedLanguage = string.IsNullOrWhiteSpace(detectedLanguage) ? null : detectedLanguage.Trim();

        if (string.IsNullOrWhiteSpace(LocationName) && !string.IsNullOrWhiteSpace(locationName))
        {
            LocationName = locationName.Trim();
        }

        entities.Clear();

        foreach (var entity in extractedEntities.Take(MaxEntities))
        {
            if (entity is not null && !entities.Any(existing => existing.MatchKey == entity.MatchKey))
            {
                entities.Add(entity);
            }
        }
    }

    /// <summary>
    /// Records the trained model's assessment. Deliberately has no power to change
    /// <see cref="Severity"/>: a model fitted to a small synthetic corpus is evidence about the
    /// model, not authority over a source that declared its own severity.
    /// </summary>
    public void RecordModelSeverity(Severity severity, double confidence, string modelVersion)
    {
        if (confidence is < 0 or > 1)
        {
            throw new DomainException("Model confidence must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(modelVersion))
        {
            throw new DomainException("A model prediction must record the model that produced it.");
        }

        ModelSeverity = severity;
        ModelSeverityConfidence = confidence;
        ModelVersion = modelVersion.Trim();
    }

    /// <summary>
    /// Whether the model disagreed with the severity the pipeline acted on. False when no prediction
    /// was made, because "no second opinion" is not a disagreement.
    /// </summary>
    public bool ModelDisagrees => ModelSeverity is { } predicted && predicted != Severity;

    /// <summary>
    /// Marks the payload as accepted for downstream processing. Separate from enrichment because an
    /// observation reaches this state whether it was enriched by a model or classified
    /// deterministically, and the pipeline treats both as validated input from here on.
    /// </summary>
    public void MarkValidated()
    {
        Status = ObservationStatus.Validated;
        FailureReason = null;
    }

    private static string? Cap(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : string.Concat(trimmed.AsSpan(0, maxLength - 1), "…");
    }

    /// <summary>
    /// Attaches deterministically resolved coordinates, with an optional note on how coarse they are.
    /// <para>
    /// The note matters as much as the position. A report placed at a country centroid and one placed
    /// at a city are both "located" as far as the type system is concerned, and drawing them
    /// identically on a globe would claim a precision the first does not have. Where the resolver
    /// knows the placement is approximate, it says so here and the dashboard repeats it.
    /// </para>
    /// </summary>
    public void ResolveLocation(GeoLocation location, string? precisionNote = null)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        LocationName ??= location.Name;
        Status = ObservationStatus.LocationResolved;
        LocationResolutionNote = Cap(precisionNote, 500);
    }

    /// <summary>
    /// Marks that no trustworthy coordinates were available. The observation stays in the
    /// system and remains correlatable; it simply is not placed on the map.
    /// </summary>
    public void MarkLocationUnresolved(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A location resolution failure requires a reason.");
        }

        Location = null;
        Status = ObservationStatus.LocationUnresolved;
        LocationResolutionNote = reason.Trim();
    }

    public void LinkToIncident(Guid incidentId)
    {
        if (incidentId == Guid.Empty)
        {
            throw new DomainException("An incident identifier is required.");
        }

        IncidentId = incidentId;
        Status = ObservationStatus.Correlated;
        FailureReason = null;
    }

    /// <summary>
    /// Records that an identical report was already accepted. The observation is kept so the
    /// duplicate delivery remains auditable rather than silently discarded.
    /// </summary>
    public void MarkDuplicate(Guid originalObservationId)
    {
        if (originalObservationId == Guid.Empty)
        {
            throw new DomainException("A duplicate must reference the observation it repeats.");
        }

        if (originalObservationId == Id)
        {
            throw new DomainException("An observation cannot be a duplicate of itself.");
        }

        DuplicateOfObservationId = originalObservationId;

        // A repeat delivery is evidence of nothing, so it belongs to no incident. Usually this is
        // already true, because a duplicate is normally recognised before correlation runs. It is
        // not true when the race is lost at the commit: by then this observation has been linked to
        // an incident that was rolled back with the failed save, and keeping the link would leave
        // the row pointing at a record that does not exist.
        IncidentId = null;
        Status = ObservationStatus.Duplicate;
        FailureReason = null;
    }

    public void MarkPersisted()
    {
        if (IncidentId is null)
        {
            throw new DomainException("An observation must be correlated with an incident before it is marked persisted.");
        }

        Status = ObservationStatus.Persisted;
        FailureReason = null;
    }

    public void AdvanceTo(ObservationStatus status)
    {
        if (status is ObservationStatus.Failed or ObservationStatus.Received)
        {
            throw new DomainException("Use MarkFailed for failures and do not reset an observation to received.");
        }

        Status = status;
        FailureReason = null;
    }

    public void MarkFailed(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A failure reason is required.");
        }

        Status = ObservationStatus.Failed;
        FailureReason = reason.Trim();
    }
}
