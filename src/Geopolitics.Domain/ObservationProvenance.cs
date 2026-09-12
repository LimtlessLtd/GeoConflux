namespace Geopolitics.Domain;

/// <summary>
/// Where a record came from, which is a different question from whether it is true.
/// <para>
/// The dashboard has always had to distinguish synthetic data from real reporting, and a boolean did
/// that adequately while there were only two ways in. There are now three, and the one this enum
/// exists for is the middle case: a collection bundle is real reporting gathered at a stated moment,
/// so calling it demo data would be a lie in one direction and calling it a live feed would be a lie
/// in the other.
/// </para>
/// </summary>
public enum ObservationProvenance
{
    /// <summary>
    /// The recorded replay stream. Synthetic, and labelled as such everywhere it travels.
    /// <para>
    /// Zero so that a record written before this property existed reads as the safer of the two
    /// wrong answers: mislabelling real reporting as a demo understates the dashboard, where the
    /// reverse would misrepresent synthetic data as fact.
    /// </para>
    /// </summary>
    Recorded = 0,

    /// <summary>A live adapter reached its provider during this run. Fresh as of the run.</summary>
    Polled,

    /// <summary>
    /// An OSINT agent gathered it and recorded a bundle. Real reporting, as of the collection time
    /// rather than as of now — which is why that time is published rather than assumed.
    /// </summary>
    Collected,
}
