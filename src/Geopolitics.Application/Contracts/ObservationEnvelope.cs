using Geopolitics.Domain;

namespace Geopolitics.Application.Contracts;

/// <summary>
/// The unit of work handed from an ingestion adapter to the processing queue. Adapters translate
/// their provider-specific payload into this shape; everything downstream is provider-agnostic.
/// </summary>
/// <remarks>
/// Fields prefixed <c>Declared</c> are claims made by the source. They are treated as hints,
/// not as verified facts, and the pipeline records where each value came from.
/// </remarks>
public sealed record ObservationEnvelope
{
    /// <summary>Human-readable name of the originating source, e.g. <c>replay:reuters</c>.</summary>
    public required string SourceName { get; init; }

    public required ObservationKind Kind { get; init; }

    /// <summary>The source payload text used for enrichment and fingerprinting.</summary>
    public required string Content { get; init; }

    /// <summary>Stable identifier assigned by the source, when it provides one.</summary>
    public string? SourceIdentifier { get; init; }

    public string? Title { get; init; }

    /// <summary>When the source says the event happened. Falls back to arrival time when absent.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public string? DeclaredLocationName { get; init; }

    /// <summary>
    /// Coordinates supplied directly by a structured provider. These are authoritative because
    /// they come from the provider's own record, not from free-text inference.
    /// </summary>
    public double? DeclaredLatitude { get; init; }

    public double? DeclaredLongitude { get; init; }

    public string? DeclaredCountryCode { get; init; }

    public EventType? DeclaredEventType { get; init; }

    public Severity? DeclaredSeverity { get; init; }

    /// <summary>
    /// Which intake path produced this envelope. Recorded by default, because that is the safer of
    /// the two wrong answers if an adapter ever forgets to say: understating a real report costs
    /// visibility, while presenting synthetic data as real costs the reader's trust.
    /// </summary>
    public ObservationProvenance Provenance { get; init; } = ObservationProvenance.Recorded;

    /// <summary>When the collection run that produced this happened. Set only for collected items.</summary>
    public DateTimeOffset? CollectedAt { get; init; }

    /// <summary>Correlation identifier carried through the pipeline for tracing.</summary>
    public string? TraceId { get; init; }
}
