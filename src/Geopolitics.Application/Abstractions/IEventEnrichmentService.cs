using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <param name="SourceName">Who reported it, included so the model can weigh the framing of the text.</param>
/// <param name="Title">Headline, when the source supplied one.</param>
/// <param name="Content">The payload text. Untrusted, and passed to the model as data.</param>
public sealed record EnrichmentRequest(string SourceName, string? Title, string Content);

/// <param name="Enrichment">The validated result, or <see langword="null"/> when nothing usable was produced.</param>
/// <param name="Outcome">Whether the attempt succeeded, failed validation, failed at the provider, or was skipped.</param>
/// <param name="Provider">Provider identifier, recorded for traceability.</param>
/// <param name="Model">Model identifier, recorded for traceability.</param>
/// <param name="PromptVersion">Version of the prompt that produced this.</param>
/// <param name="SchemaVersion">Version of the contract the response was validated against.</param>
/// <param name="Attempts">Provider calls made, including any repair retry.</param>
/// <param name="LatencyMilliseconds">Wall-clock time for the whole attempt.</param>
/// <param name="StructuredOutput">The validated payload as JSON, for the audit record. Never a reasoning transcript.</param>
/// <param name="Error">Why the attempt failed, when it did.</param>
public sealed record EnrichmentResult(
    ValidatedEnrichment? Enrichment,
    AiInferenceOutcome Outcome,
    string Provider,
    string Model,
    string PromptVersion,
    int SchemaVersion,
    int Attempts,
    double LatencyMilliseconds,
    string? StructuredOutput,
    string? Error)
{
    public bool IsSuccess => Enrichment is not null && Outcome == AiInferenceOutcome.Succeeded;

    public static EnrichmentResult Skipped(string reason) => new(
        null,
        AiInferenceOutcome.Skipped,
        "none",
        string.Empty,
        EnrichmentPrompt.Version,
        EnrichmentContract.SchemaVersion,
        Attempts: 0,
        LatencyMilliseconds: 0,
        StructuredOutput: null,
        Error: reason);
}

/// <summary>
/// Semantic enrichment of an observation: translation, summarisation, classification, severity
/// assessment, entity extraction, and location <em>naming</em>.
/// <para>
/// Implementations must not throw for provider failures. A model being slow, unreachable, or wrong
/// is an expected operating condition, and the pipeline needs a reported outcome it can fall back
/// from, not an exception that would cost the observation.
/// </para>
/// </summary>
public interface IEventEnrichmentService
{
    Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Stores the audit trail of enrichment attempts. Kept separate from the observation repository
/// because inferences are append-only telemetry about processing, not part of the evidence record.
/// </summary>
public interface IAiInferenceRepository
{
    Task AddAsync(AiInference inference, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiInference>> ListByObservationAsync(Guid observationId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
