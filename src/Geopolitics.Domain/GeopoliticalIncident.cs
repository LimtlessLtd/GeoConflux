namespace Geopolitics.Domain;

public sealed class GeopoliticalIncident
{
    private readonly List<Guid> observationIds = [];

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
        bool isDemo,
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
        IsDemo = isDemo;
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

    public bool IsDemo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int ObservationCount { get; private set; }

    public IReadOnlyCollection<Guid> ObservationIds => observationIds.AsReadOnly();

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
