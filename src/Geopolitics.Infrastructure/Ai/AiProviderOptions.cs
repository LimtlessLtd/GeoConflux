namespace Geopolitics.Infrastructure.Ai;

/// <summary>Which chat backend the enrichment service talks to.</summary>
public enum AiProviderKind
{
    /// <summary>
    /// A deterministic in-process stand-in. The default, because the specification requires the
    /// application to run and demo with no credentials of any kind.
    /// </summary>
    Mock = 0,

    /// <summary>
    /// A local Ollama daemon, reached through its own API. No credential, no network egress.
    /// </summary>
    Ollama,

    /// <summary>The OpenAI platform.</summary>
    OpenAI,

    /// <summary>An Azure OpenAI deployment.</summary>
    AzureOpenAI,
}

/// <summary>
/// Provider selection and connection settings.
/// <para>
/// <see cref="ApiKey"/> is bound from configuration like anything else, which means environment
/// variables and user secrets in development and a secret store in deployment. No key is ever
/// written to this repository, and nothing here is logged.
/// </para>
/// </summary>
public sealed class AiProviderOptions
{
    public const string SectionName = "Ai";

    public AiProviderKind Provider { get; set; } = AiProviderKind.Mock;

    /// <summary>
    /// Model or deployment name. The Ollama default is small on purpose: enrichment here is
    /// classification and summarisation over short text, which does not need a large model, and a
    /// small one keeps a local demo responsive.
    /// </summary>
    public string Model { get; set; } = "llama3.2";

    /// <summary>
    /// Base endpoint. Required for Azure OpenAI; for Ollama it defaults to the local daemon's
    /// OpenAI-compatible path, and for OpenAI it is left unset unless a proxy is in use.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Credential for the selected provider. Never committed; never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Zero by default. Enrichment is a classification task, not a creative one, and reproducibility
    /// is worth more here than variety — particularly for the evaluation harness.
    /// </summary>
    public float Temperature { get; set; }

    /// <summary>
    /// Passed to providers that honour it. It reduces run-to-run variation but does not remove it,
    /// so the evaluation harness reports model non-determinism rather than claiming to have
    /// eliminated it.
    /// </summary>
    public long? Seed { get; set; } = 1;

    /// <summary>Caps the response size, which bounds both latency and spend on a runaway generation.</summary>
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>
    /// Whether a local model that can reason before answering should be allowed to. Ollama only.
    /// <para>
    /// Off, because for this task the reasoning is pure cost. Measured on one Arabic report: 27
    /// seconds and 1,484 completion tokens with it, 2.5 seconds and 80 without, for an answer that
    /// was no better — the non-thinking run returned a correct <c>ar</c> tag where the thinking run
    /// returned "Arabic". Turn it on for a model whose deliberation is worth nine minutes a run.
    /// </para>
    /// </summary>
    public bool EnableThinking { get; set; }

    /// <summary>The endpoint actually used, applying the per-provider default where none is set.</summary>
    public string? ResolveEndpoint() => Provider switch
    {
        AiProviderKind.Ollama => string.IsNullOrWhiteSpace(Endpoint) ? OllamaChatClient.DefaultEndpoint : Endpoint.Trim(),
        _ => string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim(),
    };
}
