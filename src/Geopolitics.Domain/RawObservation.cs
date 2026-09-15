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

    /// <summary>
    /// How many conflicts one report may be recorded against, on either list. Multi-membership is
    /// real — a strike on shipping belongs to the war it is part of and to the wider confrontation —
    /// and this is a bound on it rather than a denial of it.
    /// </summary>
    public const int MaxConflicts = 8;

    private readonly List<ExtractedEntity> entities = [];
    private readonly List<string> conflictKeys = [];
    private readonly List<string> conflictCandidateKeys = [];

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
        DateTimeOffset? collectedAt = null,
        SourceAttribution? attribution = null,
        string? declaredLanguage = null)
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

        var source = attribution ?? SourceAttribution.Published;
        Tier = source.Tier;
        Platform = source.Platform;
        Channel = source.Channel;

        // Recorded at intake rather than waiting for enrichment to guess. A collector quoting an
        // Arabic post has stated the language of the text it quoted; a model reading that text
        // later infers it. Both answers are useful and the stated one is the stronger claim, which
        // is why enrichment fills this only when it is empty.
        DeclaredLanguage = string.IsNullOrWhiteSpace(declaredLanguage) ? null : declaredLanguage.Trim();
        DetectedLanguage = DeclaredLanguage;

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
    /// Whether an organisation stands behind this or an account does. Set at intake and never
    /// revised, because it is a fact about the source rather than an assessment of the claim.
    /// </summary>
    public SourceTier Tier { get; private set; }

    /// <summary>The open platform a post appeared on. Null for everything that is not a post.</summary>
    public string? Platform { get; private set; }

    /// <summary>The account or channel that posted it. Null for everything that is not a post.</summary>
    public string? Channel { get; private set; }

    /// <summary>
    /// Who said this, in the form the corroboration gate reasons about. Reassembled rather than
    /// stored, so it cannot disagree with the three columns it is built from.
    /// </summary>
    public SourceAttribution Attribution => new(Tier, Platform, Channel);

    /// <summary>
    /// Language the source itself stated, as opposed to the one enrichment inferred. Kept apart from
    /// <see cref="DetectedLanguage"/> so a coverage count can say whether the breakdown rests on
    /// what sources declared or on what a model guessed — those deserve different confidence.
    /// </summary>
    public string? DeclaredLanguage { get; private set; }

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
    /// True only for a synthetic record. Nothing in the application can produce one since ADR 040,
    /// so this should always be false in a running system; it is kept because the labelling is the
    /// safety net and a net that is removed once it stops catching anything is not a net. Derived
    /// rather than stored, so it cannot drift out of agreement with <see cref="Provenance"/> —
    /// which is exactly what two columns would do.
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
    /// Whether the English text on this record is a translation, the source's own English, or
    /// missing because nothing translated it. Recorded rather than inferred from the language tag;
    /// see <see cref="TranslationState"/> for why that distinction had to be made explicit.
    /// </summary>
    public TranslationState Translation { get; private set; }

    /// <summary>
    /// The headline in English, when something rendered it. Null whenever <see cref="Translation"/>
    /// is not <see cref="TranslationState.MachineTranslated"/>.
    /// <para>
    /// Kept beside <see cref="Title"/> rather than replacing it. The source's own headline is part
    /// of the evidence and a reader checking a translated claim has nothing to check it against once
    /// it is overwritten — which is exactly what happened to the body text before this existed.
    /// </para>
    /// </summary>
    public string? TranslatedTitle { get; private set; }

    /// <summary>
    /// The body in English, when something rendered it. Null whenever <see cref="Translation"/> is
    /// not <see cref="TranslationState.MachineTranslated"/>.
    /// <para>
    /// Separate from <see cref="Summary"/> on purpose. That field is the summary the pipeline acted
    /// on — the one correlation compares and the deduplicator reads — and a model that reported low
    /// confidence in its <em>classification</em> is deliberately not allowed to replace it. But a
    /// shaky guess at the category says nothing about the quality of the English it produced, and
    /// throwing that English away would leave a reader with Arabic on the one path where a
    /// translation had already been paid for.
    /// </para>
    /// </summary>
    public string? TranslatedSummary { get; private set; }

    /// <summary>
    /// What produced the English text, for example <c>ai:ollama/llama3.2</c>. Null when nothing did.
    /// Stored for the same reason <see cref="ClassificationMethod"/> is: a reader is entitled to know
    /// whether the English they are reading came from a model, and from which one.
    /// </summary>
    public string? TranslationMethod { get; private set; }

    /// <summary>
    /// Whether English text is available at all, by either route. False means the dashboard has only
    /// the source's own language to show, and must say so.
    /// </summary>
    public bool HasEnglishText => Translation != TranslationState.NotTranslated;

    /// <summary>
    /// One or two sentences explaining the severity assessment. Bounded on purpose: this is a
    /// concise, structured justification for application use, not a reasoning transcript.
    /// </summary>
    public string? SeverityRationale { get; private set; }

    /// <summary>Named actors the enrichment step found in the payload. Empty when none were found.</summary>
    public IReadOnlyList<ExtractedEntity> Entities => entities.AsReadOnly();

    /// <summary>
    /// Conflicts this report was held to belong to. Usually none or one; more than one is expected
    /// and correct, which is why counts per conflict do not sum to the number of observations.
    /// </summary>
    public IReadOnlyList<string> ConflictKeys => conflictKeys.AsReadOnly();

    /// <summary>
    /// Conflicts whose geography contains this report but which nothing in it identifies. Kept
    /// because "inside the area of four wars, and no way to tell which" is a fact worth storing, and
    /// discarding it would make an unassigned report indistinguishable from one nothing covers.
    /// </summary>
    public IReadOnlyList<string> ConflictCandidateKeys => conflictCandidateKeys.AsReadOnly();

    /// <summary>
    /// Why this report belongs where it does. One value rather than one per conflict, because the
    /// assignment only ever takes the conflicts that matched at the strongest available basis — a
    /// report is not assigned to one conflict because its source coded it and to another because it
    /// happened nearby.
    /// </summary>
    public ConflictMatchBasis? ConflictBasis { get; private set; }

    /// <summary>
    /// Why this report belongs to no conflict, when it belongs to none. Stored rather than derived,
    /// because the reasons differ in what they say about this system: never placed, placed somewhere
    /// no coded conflict reaches, and placed somewhere four of them overlap are three different
    /// facts and only one of them is a gap in coverage.
    /// </summary>
    public string? ConflictNote { get; private set; }

    /// <summary>Whether this report was placed in a conflict at all.</summary>
    public bool IsAssignedToConflict => conflictKeys.Count > 0;

    /// <summary>
    /// What, if anything, this report says about who <em>holds</em> the place it describes.
    /// <para>
    /// Almost always <see cref="ControlSignal.None"/>, including for nearly every report of fighting,
    /// and that is the correct answer rather than a gap: an actor fighting somewhere is evidence
    /// about that place and is not a claim to hold it. See [ADR 037].
    /// </para>
    /// </summary>
    public ControlSignal ControlSignal { get; private set; }

    /// <summary>
    /// The actor the control signal is about, as the source named them.
    /// <para>
    /// Stored as the source's own wording rather than resolved to a register key. A coding project
    /// names parties its own way — UCDP writes "Government of Iran" where reporting writes something
    /// else entirely — and normalising here would throw away the only text a reader could check the
    /// assertion against.
    /// </para>
    /// </summary>
    public string? ControlActor { get; private set; }

    /// <summary>
    /// Where the control signal came from, which is what decides the weight it can carry. Null
    /// exactly when <see cref="ControlSignal"/> is <see cref="ControlSignal.None"/>.
    /// </summary>
    public ControlEvidenceBasis? ControlBasis { get; private set; }

    /// <summary>Whether this report is usable as evidence about control at all.</summary>
    public bool CarriesControlSignal => ControlSignal != ControlSignal.None;

    /// <summary>
    /// Records what this report says about control, and about whom.
    /// <para>
    /// Refuses a signal with no actor. "Territory changed hands" without naming who took it is not
    /// evidence of control by anybody, and storing it would put a row into the assessment that could
    /// never support an assertion and could never be checked.
    /// </para>
    /// </summary>
    public void RecordControlSignal(ControlSignal signal, string? actor, ControlEvidenceBasis basis)
    {
        if (signal == ControlSignal.None)
        {
            ControlSignal = ControlSignal.None;
            ControlActor = null;
            ControlBasis = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new DomainException(
                "A control signal must name the actor it is about; an unattributed one cannot support an assertion.");
        }

        if (basis == ControlEvidenceBasis.None)
        {
            throw new DomainException("A control signal must record where it came from.");
        }

        ControlSignal = signal;
        ControlActor = Cap(actor.Trim(), MaxControlActorLength);
        ControlBasis = basis;
    }

    /// <summary>Generous enough for a coded party name, which is the longest form these take.</summary>
    public const int MaxControlActorLength = 200;

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
        AdoptDetectedLanguage(detectedLanguage);
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

        AdoptDetectedLanguage(detectedLanguage);

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
    /// Records which conflicts this report belongs to, which ones it might belong to, and — when it
    /// belongs to none — why not.
    /// <para>
    /// All three go on together because they are one answer. Storing the memberships without the
    /// candidates would turn "four wars overlap here and nothing says which" into silence, and
    /// silence is the one reading of an unassigned report that is definitely wrong.
    /// </para>
    /// </summary>
    public void AssignConflicts(
        IEnumerable<string> keys,
        ConflictMatchBasis? basis,
        IEnumerable<string> candidates,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(candidates);

        conflictKeys.Clear();
        conflictCandidateKeys.Clear();

        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && conflictKeys.Count < MaxConflicts
                && !conflictKeys.Contains(key.Trim(), StringComparer.Ordinal))
            {
                conflictKeys.Add(key.Trim());
            }
        }

        foreach (var key in candidates)
        {
            if (!string.IsNullOrWhiteSpace(key)
                && conflictCandidateKeys.Count < MaxConflicts
                && !conflictCandidateKeys.Contains(key.Trim(), StringComparer.Ordinal))
            {
                conflictCandidateKeys.Add(key.Trim());
            }
        }

        if (conflictKeys.Count > 0 && basis is null or ConflictMatchBasis.None)
        {
            throw new DomainException("A conflict assignment must record what it rests on.");
        }

        ConflictBasis = conflictKeys.Count == 0 ? null : basis;
        ConflictNote = Cap(note, 500);
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

    /// <summary>
    /// Takes the model's reading of the language only where the source stated none.
    /// <para>
    /// The same rule the proposed place name follows, for the same reason: a language the collector
    /// declared is a fact about the record, and a language inferred from prose is a reading of it.
    /// Letting the inference win would also mean a model that answered with nothing could erase a
    /// stated value, which is the case that actually bites — it turns a known Arabic item into an
    /// item of unknown language, and the coverage count then reports one fewer language read than
    /// was read.
    /// </para>
    /// </summary>
    /// <summary>Generous enough for a translated headline, which runs longer than the original.</summary>
    public const int MaxTranslatedTitleLength = 400;

    /// <summary>Matches the bound on the summary the enrichment contract asks for.</summary>
    public const int MaxTranslatedSummaryLength = 1200;

    /// <summary>
    /// Records what, if anything, rendered this observation into English.
    /// <para>
    /// Refuses a machine translation that names no translator, for the same reason a control signal
    /// must name its actor: the whole point of storing this is that a reader can tell where the
    /// English came from, and an unattributed translation of a contested claim is the one form of it
    /// that cannot be weighed at all.
    /// </para>
    /// <para>
    /// Refuses a machine translation that carries no English text, too. A state saying a translation
    /// happened while both English fields are empty would put the dashboard back where it started —
    /// promising English and showing the source's own language.
    /// </para>
    /// </summary>
    public void RecordTranslation(
        TranslationState state,
        string? translatedTitle,
        string? translatedSummary,
        string? method)
    {
        if (state != TranslationState.MachineTranslated)
        {
            // Nothing to attribute and nothing to store. Both the "already English" and the
            // "nothing translated it" cases are answered by the source text alone.
            Translation = state;
            TranslatedTitle = null;
            TranslatedSummary = null;
            TranslationMethod = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new DomainException("A translation must record what produced it.");
        }

        var title = Cap(translatedTitle, MaxTranslatedTitleLength);
        var summary = Cap(translatedSummary, MaxTranslatedSummaryLength);

        if (title is null && summary is null)
        {
            throw new DomainException("A translation must carry the English text it claims to have produced.");
        }

        Translation = TranslationState.MachineTranslated;
        TranslatedTitle = title;
        TranslatedSummary = summary;
        TranslationMethod = method.Trim();
    }

    private void AdoptDetectedLanguage(string? detectedLanguage)
    {
        if (!string.IsNullOrWhiteSpace(DeclaredLanguage) || string.IsNullOrWhiteSpace(detectedLanguage))
        {
            return;
        }

        DetectedLanguage = detectedLanguage.Trim();
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

    /// <summary>
    /// Records that this claim stands alone and may not open an incident.
    /// <para>
    /// The observation is kept in full — stored, placed, classified, and shown. What is withheld is
    /// the assertion that the thing it describes happened, because a single post is evidence that a
    /// post exists and nothing more. That distinction is the whole of the corroboration rule, and
    /// putting it in a status rather than in a filter is deliberate: a claim that is hidden is a
    /// claim nobody can corroborate, and hiding it would also quietly conceal how much of the
    /// picture rests on unsupported posts.
    /// </para>
    /// <para>
    /// Refuses to hold published reporting. A wire story or a coded dataset record stands on its
    /// own, and a bug that routed one through here would look exactly like a quiet outage — the map
    /// would keep filling with claims while incidents stopped opening, and nothing would say why.
    /// </para>
    /// </summary>
    public void HoldAsUncorroborated()
    {
        if (Tier != SourceTier.UserGenerated)
        {
            throw new DomainException(
                "Only a user-generated claim may be held for corroboration; published reporting stands on its own.");
        }

        IncidentId = null;
        Status = ObservationStatus.Uncorroborated;
        FailureReason = null;
    }

    /// <summary>Whether this is a claim currently waiting for a second source.</summary>
    public bool IsHeldForCorroboration => Status == ObservationStatus.Uncorroborated;

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

        if (status is ObservationStatus.Uncorroborated)
        {
            // Holding a claim carries an invariant this method cannot check, so it is not reachable
            // as a plain status assignment.
            throw new DomainException("Use HoldAsUncorroborated to hold a claim for corroboration.");
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
