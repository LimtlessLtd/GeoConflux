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
    /// <summary>Human-readable name of the originating source, e.g. <c>rss:un-news</c>.</summary>
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

    /// <summary>
    /// How precisely the provider says its own coordinates describe the event, when it says.
    /// <para>
    /// Curated event datasets publish this: ACLED codes <c>geo_precision</c>, UCDP codes
    /// <c>where_prec</c>, and both mean the same thing — whether the coordinate is the event, the
    /// town it happened in, or the centroid of a province standing in for it. Discarding that and
    /// treating every borrowed coordinate as exact is the honesty problem this field exists to avoid:
    /// a provincial centroid drawn as confidently as a grid reference is a claim the record does not
    /// make.
    /// </para>
    /// <para>
    /// Null means the provider stated nothing, which is read as exact — correct for a satellite
    /// instrument geolocating a pixel, which is the one source here that genuinely measures a
    /// position rather than assigning one.
    /// </para>
    /// </summary>
    public LocationPrecision? DeclaredPrecision { get; init; }

    public string? DeclaredCountryCode { get; init; }

    /// <summary>
    /// The conflict the source's own coding assigned this to, in register form — <c>ucdp:13243</c>.
    /// <para>
    /// Set only by the adapters that read a conflict-coding project, which is where this field is the
    /// strongest statement available: cataloguing organised violence into named conflicts is that
    /// project's whole job, and nothing this system infers from the same record improves on it. Every
    /// other source leaves it null and is assigned by predicate or by model instead.
    /// </para>
    /// </summary>
    public string? DeclaredConflictKey { get; init; }

    /// <summary>
    /// What the source's own coding says about who <em>holds</em> the place, as distinct from what
    /// happened there.
    /// <para>
    /// Set only by adapters reading a conflict-coding project, for the same reason
    /// <see cref="DeclaredConflictKey"/> is: a coder asserting that territory changed hands is an
    /// assessment made by a named organisation against published criteria, and nothing this system
    /// infers from the same record improves on it. Every other source leaves it null and is
    /// classified downstream, or not at all.
    /// </para>
    /// </summary>
    public ControlSignal? DeclaredControlSignal { get; init; }

    /// <summary>The actor that signal is about, as the coding names them.</summary>
    public string? DeclaredControlActor { get; init; }

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

    /// <summary>
    /// Who is standing behind this: an organisation, or an account. Published by default, because an
    /// adapter that forgets to say is far more likely to be a wire or a dataset than a social post,
    /// and the wrong default in the other direction would silently hold back reporting nobody meant
    /// to gate.
    /// </summary>
    public SourceAttribution Attribution { get; init; } = SourceAttribution.Published;

    /// <summary>
    /// BCP-47 tag the source itself stated for the original text, where it stated one. Distinct from
    /// the language enrichment detects: this is a fact the record carries, and it survives a model
    /// being unavailable, disabled, or wrong.
    /// </summary>
    public string? DeclaredLanguage { get; init; }

    /// <summary>Correlation identifier carried through the pipeline for tracing.</summary>
    public string? TraceId { get; init; }
}
