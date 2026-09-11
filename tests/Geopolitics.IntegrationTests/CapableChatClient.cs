using Microsoft.Extensions.AI;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Stands in for a provider that <em>can</em> read the text it is given.
/// <para>
/// The shipped stand-in cannot: it matches English keywords and identifies scripts, so on an Arabic
/// report it correctly reports low confidence and the pipeline keeps its deterministic
/// classification. That is the right offline behaviour and it is asserted elsewhere, but it means
/// the offline default cannot demonstrate the translate-enrich-locate path end to end.
/// </para>
/// <para>
/// This client supplies a confident, schema-valid response so that path can be tested through the
/// real host. It still names a place rather than positioning one, so the deterministic resolver is
/// exercised rather than bypassed.
/// </para>
/// </summary>
public sealed class CapableChatClient : IChatClient
{
    private const string Response = """
    {
      "schemaVersion": 1,
      "language": "ar",
      "summary": "A cargo vessel was approached by small craft near Bab-el-Mandeb. No injuries were reported.",
      "eventType": "PIRACY",
      "severity": "MEDIUM",
      "confidence": 0.87,
      "severityRationale": "An approach was reported without injury or boarding.",
      "locations": [{ "name": "Bab-el-Mandeb", "country": "Yemen" }],
      "entities": [{ "name": "Meridian Shipping", "type": "ORGANISATION" }]
    }
    """;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Response)) { ModelId = "capable-stub" });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The enrichment service does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // No resources are held.
    }
}
