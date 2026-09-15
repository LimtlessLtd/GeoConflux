namespace Geopolitics.Domain;

public sealed class GeopoliticalIncident
{
    /// <summary>
    /// A cap on the actors one incident accumulates. Entities come from extraction over untrusted
    /// text, and an incident that draws evidence from many reports would otherwise grow this list
    /// without limit.
    /// </summary>
    public const int MaxEntityKeys = 40;

    private readonly List<Guid> observationIds = [];
    private readonly List<string> entityKeys = [];

    private GeopoliticalIncident()
    {
        Title = string.Empty;
        Summary = string.Empty;
    }

    public GeopoliticalIncident(
        Guid id,
        string title,
        string summary,
        EventType eventType,
        Severity severity,
        DateTimeOffset occurredAt,
        GeoLocation? location,
        ObservationProvenance provenance,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("An incident identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("An incident title is required.");
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainException("An incident summary is required.");
        }

        Id = id;
        Title = title.Trim();
        Summary = summary.Trim();
        EventType = eventType;
        Severity = severity;
        OccurredAt = occurredAt;
        Location = location;
        Provenance = provenance;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Title { get; private set; }

    public string Summary { get; private set; }

    public EventType EventType { get; private set; }

    public Severity Severity { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public GeoLocation? Location { get; private set; }

    /// <summary>Which intake path the evidence behind this incident came by.</summary>
    public ObservationProvenance Provenance { get; private set; }

    /// <summary>
    /// True only when this was assembled from synthetic evidence, which nothing in the application
    /// produces since ADR 040. Retained as the labelling safety net.
    /// </summary>
    public bool IsDemo => Provenance == ObservationProvenance.Recorded;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int ObservationCount { get; private set; }

    public IReadOnlyCollection<Guid> ObservationIds => observationIds.AsReadOnly();

    /// <summary>
    /// Lower-cased names of every actor this incident's evidence has mentioned, accumulated as
    /// reports are linked.
    /// <para>
    /// Held on the incident rather than recomputed from its observations because correlation asks
    /// "does this new report name anyone already involved" on the hot path, once per candidate. The
    /// union is also the more truthful answer: an incident's actors are everyone its evidence has
    /// named, not whoever the most recent outlet happened to mention.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string> EntityKeys => entityKeys.AsReadOnly();

    /// <summary>
    /// Confidence of the best-supported assessment among this incident's evidence, on a 0-1 scale.
    /// The dashboard shows a category next to this number rather than alone, so a weakly-supported
    /// incident reads as weakly supported.
    /// </summary>
    public double ClassificationConfidence { get; private set; }

    /// <summary>What produced that assessment, for example <c>keyword</c> or <c>ai:Ollama/llama3.2</c>.</summary>
    public string ClassificationMethod { get; private set; } = "none";

    /// <summary>
    /// Adopts an assessment only when it is better supported than the one already held.
    /// <para>
    /// Confidence therefore tracks the strongest evidence rather than the most recent. A later,
    /// vaguer report about an incident should not weaken a well-supported classification, and an
    /// incident's stated confidence should not oscillate as reports trickle in.
    /// </para>
    /// </summary>
    public bool RecordAssessment(double confidence, string method, DateTimeOffset recordedAt)
    {
        if (confidence is < 0 or > 1)
        {
            throw new DomainException("Classification confidence must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new DomainException("An assessment must record the method that produced it.");
        }

        if (confidence <= ClassificationConfidence && ClassificationMethod != "none")
        {
            return false;
        }

        ClassificationConfidence = confidence;
        ClassificationMethod = method.Trim();
        UpdatedAt = recordedAt;
        return true;
    }

    public bool LinkObservation(Guid observationId, DateTimeOffset linkedAt)
    {
        if (observationId == Guid.Empty)
        {
            throw new DomainException("An observation identifier is required.");
        }

        if (observationIds.Contains(observationId))
        {
            return false;
        }

        observationIds.Add(observationId);
        ObservationCount++;
        UpdatedAt = linkedAt;
        return true;
    }

    /// <summary>
    /// Adds any actors this incident has not already recorded. Returns the number newly added, which
    /// is zero for a report that names nobody new.
    /// </summary>
    public int MergeEntities(IEnumerable<ExtractedEntity> entities, DateTimeOffset mergedAt)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var added = 0;

        foreach (var entity in entities)
        {
            if (entity is null || entityKeys.Count >= MaxEntityKeys)
            {
                continue;
            }

            if (!entityKeys.Contains(entity.MatchKey, StringComparer.Ordinal))
            {
                entityKeys.Add(entity.MatchKey);
                added++;
            }
        }

        if (added > 0)
        {
            UpdatedAt = mergedAt;
        }

        return added;
    }

    public void Reassess(EventType eventType, Severity severity, string summary, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainException("An incident summary is required.");
        }

        EventType = eventType;
        Severity = severity;
        Summary = summary.Trim();
        UpdatedAt = updatedAt;
    }

    public void ResolveLocation(GeoLocation location, DateTimeOffset resolvedAt)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        UpdatedAt = resolvedAt;
    }
}
