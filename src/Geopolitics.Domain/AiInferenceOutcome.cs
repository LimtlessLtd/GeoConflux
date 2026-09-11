namespace Geopolitics.Domain;

/// <summary>
/// Why an enrichment attempt ended the way it did. The distinction matters operationally: a provider
/// that is down is an availability problem, whereas a provider that answers with output failing
/// schema validation is a model-quality problem, and the two demand different responses.
/// </summary>
public enum AiInferenceOutcome
{
    /// <summary>The model answered and the answer passed schema validation.</summary>
    Succeeded = 0,

    /// <summary>The model answered, but the answer was unusable and could not be repaired.</summary>
    ValidationFailed,

    /// <summary>The provider errored, timed out, or was unreachable.</summary>
    ProviderFailed,

    /// <summary>Enrichment was not attempted, for example because it is disabled by configuration.</summary>
    Skipped,
}
