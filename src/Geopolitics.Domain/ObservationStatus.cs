namespace Geopolitics.Domain;

public enum ObservationStatus
{
    Received = 0,
    Normalised,
    Enriched,
    Validated,
    LocationUnresolved,
    LocationResolved,
    Duplicate,
    Correlated,
    Persisted,
    Failed,
}
