using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Geopolitics.Infrastructure.Ai;

/// <summary>
/// Talks to a local Ollama daemon through its own <c>/api/chat</c> endpoint rather than through its
/// OpenAI-compatible shim.
/// <para>
/// The shim was used first, because reusing the OpenAI client meant no second client library to keep
/// current. It cannot express the one parameter that decides whether local enrichment is usable.
/// Current Qwen models reason before answering, and the reasoning is the entire cost: measured on an
/// RTX 2060 against one Arabic report, the same model answered in <b>27 seconds and 1,484 completion
/// tokens</b> through the shim and in <b>2.5 seconds and 80 tokens</b> here, with
/// <c>think: false</c> set. Over a 259-observation run that is the difference between two hours and
/// nine minutes.
/// </para>
/// <para>
/// The shim does not merely lack the parameter, it accepts and ignores it: sending <c>think</c> in an
/// OpenAI-shaped body changed nothing, and the <c>/no_think</c> prompt directive made the model emit
/// 3,966 tokens and no answer at all. A compatibility layer that silently discards what it is given
/// is the wrong thing to build a guarantee on, which is the second reason this exists.
/// </para>
/// <para>
/// Structured output is passed as Ollama's <c>format</c> field, taking the same JSON schema the
/// contract hands every other provider. The response is validated identically on the way back, so
/// nothing here is trusted merely for having come from a local process.
/// </para>
/// </summary>
public sealed class OllamaChatClient : IChatClient
{
    /// <summary>Ollama needs no credential and is not reached over the public internet.</summary>
    public const string DefaultEndpoint = "http://localhost:11434";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient;
    private readonly AiProviderOptions providerOptions;
    private readonly ChatClientMetadata metadata;

    public OllamaChatClient(HttpClient httpClient, AiProviderOptions providerOptions)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(providerOptions);

        this.httpClient = httpClient;
        this.providerOptions = providerOptions;
        metadata = new ChatClientMetadata("ollama", new Uri(ResolveBase(providerOptions)), providerOptions.Model);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = BuildRequest(messages, options);

        using var response = await httpClient.PostAsJsonAsync(
            "api/chat",
            request,
            SerializerOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned a response that could not be deserialised.");

        var text = body.Message?.Content ?? string.Empty;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = body.Model ?? providerOptions.Model,
            FinishReason = body.Done ? ChatFinishReason.Stop : null,
            Usage = new UsageDetails
            {
                InputTokenCount = body.PromptEvalCount,
                OutputTokenCount = body.EvalCount,
                TotalTokenCount = (body.PromptEvalCount ?? 0) + (body.EvalCount ?? 0),
            },
        };
    }

    /// <summary>
    /// Satisfies the interface by returning the completed response in one update.
    /// <para>
    /// Nothing in this system streams. Enrichment validates a whole JSON document against a schema
    /// and repairs it if it fails, so a partial response has no use, and implementing real streaming
    /// would add a code path no caller exercises.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
        // The HttpClient is owned by the factory that supplied it.
    }

    /// <summary>Trims the OpenAI-compatible <c>/v1</c> suffix, so an existing endpoint setting still works.</summary>
    internal static string ResolveBase(AiProviderOptions options)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint)
            ? DefaultEndpoint
            : options.Endpoint.Trim();

        endpoint = endpoint.TrimEnd('/');

        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = endpoint[..^3];
        }

        return endpoint + "/";
    }

    private OllamaChatRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options) => new()
    {
        Model = string.IsNullOrWhiteSpace(options?.ModelId) ? providerOptions.Model : options.ModelId,
        Messages = [.. messages.Select(message => new OllamaMessage
        {
            Role = message.Role == ChatRole.System ? "system"
                : message.Role == ChatRole.Assistant ? "assistant"
                : "user",
            Content = message.Text ?? string.Empty,
        })],
        Stream = false,

        // The reason this client exists. Left configurable rather than hard-coded because a future
        // model may be worth the reasoning tokens, but the default is off: for classifying and
        // translating a short report the deliberation costs an order of magnitude and did not, in
        // testing, produce a better answer — the non-thinking runs actually returned better-formed
        // BCP-47 language tags.
        Think = providerOptions.EnableThinking ? null : false,
        Format = SchemaOf(options),
        Options = new OllamaRuntimeOptions
        {
            Temperature = options?.Temperature ?? providerOptions.Temperature,
            Seed = options?.Seed ?? providerOptions.Seed,
            NumPredict = options?.MaxOutputTokens ?? providerOptions.MaxOutputTokens,
        },
    };

    /// <summary>
    /// Hands Ollama the same JSON schema every other provider is given, when one was requested.
    /// <para>
    /// Ollama takes the schema document itself rather than the OpenAI wrapper around it, so the
    /// wrapper is unwrapped rather than a second schema being written. Two copies of a contract is
    /// how they drift.
    /// </para>
    /// </summary>
    private static JsonNode? SchemaOf(ChatOptions? options) =>
        options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema }
            ? JsonNode.Parse(schema.GetRawText())
            : null;

    private sealed record OllamaChatRequest
    {
        public required string Model { get; init; }

        public required IReadOnlyList<OllamaMessage> Messages { get; init; }

        public bool Stream { get; init; }

        /// <summary>Null omits the field, which leaves the model's own default in place.</summary>
        public bool? Think { get; init; }

        public JsonNode? Format { get; init; }

        public OllamaRuntimeOptions? Options { get; init; }
    }

    private sealed record OllamaMessage
    {
        public required string Role { get; init; }

        public required string Content { get; init; }
    }

    private sealed record OllamaRuntimeOptions
    {
        public float? Temperature { get; init; }

        public long? Seed { get; init; }

        [JsonPropertyName("num_predict")]
        public int? NumPredict { get; init; }
    }

    private sealed record OllamaChatResponse
    {
        public string? Model { get; init; }

        public OllamaMessage? Message { get; init; }

        public bool Done { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; init; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; init; }
    }
}
