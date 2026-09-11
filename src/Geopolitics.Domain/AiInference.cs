namespace Geopolitics.Domain;

/// <summary>
/// The audit record of one enrichment attempt against one observation.
/// <para>
/// It exists so that any classification shown on the dashboard can be traced back to the provider,
/// model, prompt version, and schema version that produced it, and so that failures are visible
/// rather than silently absorbed by the fallback path. Per the trust rules in the specification it
/// stores the <em>validated structured output</em> and a short rationale only: no chain-of-thought,
/// no raw provider transcript, and no credentials.
/// </para>
/// </summary>
public sealed class AiInference
{
    public const int MaxRationaleLength = 600;
    private const int MaxStructuredOutputLength = 8000;
    private const int MaxErrorLength = 1000;

    private AiInference()
    {
        Provider = string.Empty;
        Model = string.Empty;
        PromptVersion = string.Empty;
    }

    private AiInference(
        Guid id,
        Guid observationId,
        string provider,
        string model,
        string promptVersion,
        int schemaVersion,
        AiInferenceOutcome outcome,
        double? confidence,
        int attempts,
        double latencyMilliseconds,
        DateTimeOffset createdAt,
        string? structuredOutput,
        string? error)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("An inference identifier is required.");
        }

        if (observationId == Guid.Empty)
        {
            throw new DomainException("An inference must reference the observation it enriched.");
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new DomainException("An inference must record which provider produced it.");
        }

        if (string.IsNullOrWhiteSpace(promptVersion))
        {
            throw new DomainException("An inference must record the prompt version that produced it.");
        }

        if (confidence is { } value && value is < 0 or > 1)
        {
            throw new DomainException("Confidence must be between 0 and 1.");
        }

        if (latencyMilliseconds < 0)
        {
            throw new DomainException("Latency cannot be negative.");
        }

        Id = id;
        ObservationId = observationId;
        Provider = provider.Trim();

        // An empty model name is legitimate for a provider that exposes only one, so it is
        // recorded as empty rather than rejected.
        Model = model?.Trim() ?? string.Empty;
        PromptVersion = promptVersion.Trim();
        SchemaVersion = schemaVersion;
        Outcome = outcome;
        Confidence = confidence;
        Attempts = attempts;
        LatencyMilliseconds = latencyMilliseconds;
        CreatedAt = createdAt;
        StructuredOutput = Cap(structuredOutput, MaxStructuredOutputLength);
        Error = Cap(error, MaxErrorLength);
    }

    public Guid Id { get; private set; }

    /// <summary>The observation this attempt enriched. The input itself is not copied here.</summary>
    public Guid ObservationId { get; private set; }

    public string Provider { get; private set; }

    public string Model { get; private set; }

    /// <summary>Version of the prompt template used, so a change in wording is attributable.</summary>
    public string PromptVersion { get; private set; }

    /// <summary>Version of the output contract the response was validated against.</summary>
    public int SchemaVersion { get; private set; }

    public AiInferenceOutcome Outcome { get; private set; }

    /// <summary>Model-reported confidence, present only when validation succeeded.</summary>
    public double? Confidence { get; private set; }

    /// <summary>How many provider calls the attempt took, including any repair retry.</summary>
    public int Attempts { get; private set; }

    public double LatencyMilliseconds { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The validated payload as JSON. Null when nothing usable was produced.</summary>
    public string? StructuredOutput { get; private set; }

    /// <summary>Why the attempt failed, when it did.</summary>
    public string? Error { get; private set; }

    public bool IsSuccess => Outcome == AiInferenceOutcome.Succeeded;

    public static AiInference Succeeded(
        Guid id,
        Guid observationId,
        string provider,
        string model,
        string promptVersion,
        int schemaVersion,
        double confidence,
        int attempts,
        double latencyMilliseconds,
        DateTimeOffset createdAt,
        string structuredOutput)
    {
        if (string.IsNullOrWhiteSpace(structuredOutput))
        {
            throw new DomainException("A successful inference must record the output it produced.");
        }

        return new AiInference(
            id,
            observationId,
            provider,
            model,
            promptVersion,
            schemaVersion,
            AiInferenceOutcome.Succeeded,
            confidence,
            attempts,
            latencyMilliseconds,
            createdAt,
            structuredOutput,
            error: null);
    }

    public static AiInference Failed(
        Guid id,
        Guid observationId,
        string provider,
        string model,
        string promptVersion,
        int schemaVersion,
        AiInferenceOutcome outcome,
        int attempts,
        double latencyMilliseconds,
        DateTimeOffset createdAt,
        string error)
    {
        if (outcome == AiInferenceOutcome.Succeeded)
        {
            throw new DomainException("A failed inference cannot record a successful outcome.");
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            throw new DomainException("A failed inference must record why it failed.");
        }

        return new AiInference(
            id,
            observationId,
            provider,
            model,
            promptVersion,
            schemaVersion,
            outcome,
            confidence: null,
            attempts,
            latencyMilliseconds,
            createdAt,
            structuredOutput: null,
            error);
    }

    /// <summary>
    /// Truncates rather than rejects. An oversized field is a reason to store less, not a reason to
    /// lose the entire audit record of an attempt that already happened.
    /// </summary>
    private static string? Cap(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : string.Concat(trimmed.AsSpan(0, maxLength - 1), "…");
    }
}
