using Microsoft.Extensions.AI;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// A chat client that returns whatever a test tells it to, in order, and records what it was sent.
/// <para>
/// Kept separate from the shipped mock provider on purpose. The shipped mock always produces valid
/// output, which is the right behaviour for a demo and useless for testing what happens when a
/// provider misbehaves. These tests need to cause malformed output, exceptions, and hangs on demand.
/// </para>
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<IEnumerable<ChatMessage>, string>> script = new();

    /// <summary>Every conversation the client was handed, so repair prompts can be asserted on.</summary>
    public List<List<ChatMessage>> Conversations { get; } = [];

    public int CallCount => Conversations.Count;

    /// <summary>Thrown instead of answering, for provider-failure paths.</summary>
    public Exception? ThrowOnCall { get; set; }

    /// <summary>When true the client never answers, so the caller's timeout is what ends the call.</summary>
    public bool HangForever { get; set; }

    /// <summary>Completes once the client has been entered, so a timeout test cannot race it.</summary>
    public TaskCompletionSource CallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedChatClient Returns(params string[] responses)
    {
        foreach (var response in responses)
        {
            script.Enqueue(_ => response);
        }

        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = messages.ToList();
        Conversations.Add(conversation);
        CallStarted.TrySetResult();

        if (ThrowOnCall is { } exception)
        {
            throw exception;
        }

        if (HangForever)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        var text = script.Count > 0 ? script.Dequeue()(conversation) : "{}";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "scripted" };
    }

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
