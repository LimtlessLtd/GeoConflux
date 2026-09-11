namespace Geopolitics.Application.Enrichment;

/// <summary>
/// How the pipeline uses the enrichment service. Provider credentials and endpoints are not here:
/// those are infrastructure configuration, and keeping them out of the application layer means this
/// type can be read, tested, and reasoned about without touching anything secret.
/// </summary>
public sealed class EnrichmentOptions
{
    public const string SectionName = "Enrichment";

    /// <summary>
    /// Whether observations are sent for enrichment at all. Turning this off leaves the pipeline
    /// fully operational on the deterministic classifier, which is the behaviour an operator wants
    /// when a provider is costing money or misbehaving.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ceiling on one enrichment attempt including any repair retry. A model that has not answered
    /// by now is holding up a queue worker, and the deterministic fallback is immediately available.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Extra calls allowed to repair output that failed validation.
    /// <para>
    /// One, by default. A model that cannot satisfy the schema when handed the exact validation
    /// errors is unlikely to on a third attempt, and each retry costs latency and spend for an
    /// outcome the keyword classifier already covers.
    /// </para>
    /// </summary>
    public int MaxRepairAttempts { get; set; } = 1;

    /// <summary>
    /// Below this, the model's own classification is recorded but not applied: the observation keeps
    /// the deterministic classification instead. An unsure model should not overwrite a transparent
    /// heuristic with an opaque guess.
    /// </summary>
    public double MinimumAcceptedConfidence { get; set; } = 0.35;
}
