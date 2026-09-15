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
    /// Synthetic, and labelled as such everywhere it travels.
    /// <para>
    /// Nothing in the application produces this any more. The recorded stream that did was removed
    /// by ADR 040, and the value is kept for two reasons: rows written before that still carry it,
    /// and it is the safety net. An adapter that forgets to state its provenance gets this one, so a
    /// fabricated record can only ever fail towards being labelled synthetic — never away from it.
    /// The deploy fails the build if a published snapshot contains one.
    /// </para>
    /// <para>
    /// Zero so that a record written before this property existed reads as the safer of the two
    /// wrong answers: mislabelling real reporting as synthetic understates the dashboard, where the
    /// reverse would misrepresent invented data as fact.
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
