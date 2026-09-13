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

    /// <summary>
    /// A user-generated claim that nothing else supports. Fully processed and stored, drawn on the
    /// map, and deliberately attached to no incident — the pipeline believes a post exists saying
    /// this, and does not yet believe the thing it describes happened.
    /// <para>
    /// A resting state rather than a terminal one. A held claim is released the moment a second
    /// source arrives, whether that is published reporting or an independent channel, so this says
    /// "not yet" and never "no".
    /// </para>
    /// </summary>
    Uncorroborated,
    Correlated,
    Persisted,
    Failed,
}
