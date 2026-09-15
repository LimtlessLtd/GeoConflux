using System.Net;
using System.Text;
using System.Text.Json;
using Geopolitics.Application.Enrichment;
using Geopolitics.Infrastructure.Ai;
using Microsoft.Extensions.AI;

namespace Geopolitics.UnitTests;

/// <summary>
/// The local client exists for one reason: Ollama's OpenAI-compatible shim cannot turn off a
/// reasoning model's thinking pass, and on this workload that pass is the whole cost. Measured on
/// one Arabic report against qwen3.5:4b, 27 seconds and 1,484 completion tokens through the shim
/// against 2.5 seconds and 80 tokens through the native endpoint.
/// <para>
/// So these tests are mostly about the request: what is actually put on the wire is the thing that
/// was wrong before, and it is invisible from the outside because the shim accepted the parameter
/// and ignored it.
/// </para>
/// </summary>
public sealed class OllamaChatClientTests
{
    [Fact]
    public async Task ThinkingIsTurnedOffByDefault()
    {
        var (client, recorder) = Build(new AiProviderOptions { Model = "qwen3.5:4b" });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A report.")], null, CancellationToken.None);

        Assert.False(recorder.Body!.RootElement.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task ThinkingCanBeTurnedBackOnForAModelWorthTheTokens()
    {
        var (client, recorder) = Build(new AiProviderOptions { Model = "qwen3.5:4b", EnableThinking = true });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A report.")], null, CancellationToken.None);

        // Absent rather than true: omitting the field leaves the model's own default in place, which
        // is what "do not interfere" should mean.
        Assert.False(recorder.Body!.RootElement.TryGetProperty("think", out _));
    }

    [Fact]
    public async Task TheContractSchemaIsSentAsOllamaFormatRatherThanRewritten()
    {
        // The schema travels as the contract's own document. A second copy written by hand for this
        // provider is how two definitions of one contract start to disagree.
        var (client, recorder) = Build(new AiProviderOptions());

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "A report.")],
            new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(EnrichmentContract.ResponseSchema, "enrichment"),
            },
            CancellationToken.None);

        var format = recorder.Body!.RootElement.GetProperty("format");
        var properties = format.GetProperty("properties");

        Assert.True(properties.TryGetProperty("titleEnglish", out _));
        Assert.True(properties.TryGetProperty("translated", out _));

        // The coordinate prohibition of ADR 005 is structural, and it has to survive the trip.
        var locationProperties = properties.GetProperty("locations").GetProperty("items").GetProperty("properties");
        Assert.False(locationProperties.TryGetProperty("latitude", out _));
    }

    [Fact]
    public async Task NoFormatIsSentWhenNoSchemaWasAskedFor()
    {
        var (client, recorder) = Build(new AiProviderOptions());

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A report.")], null, CancellationToken.None);

        Assert.False(recorder.Body!.RootElement.TryGetProperty("format", out _));
    }

    [Fact]
    public async Task GenerationBoundsAreCarriedIntoOllamaRuntimeOptions()
    {
        var (client, recorder) = Build(new AiProviderOptions { MaxOutputTokens = 512, Seed = 7, Temperature = 0 });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A report.")], null, CancellationToken.None);

        var options = recorder.Body!.RootElement.GetProperty("options");

        // num_predict is the cap that stops a runaway generation running to the context limit, which
        // is exactly what happened when the thinking pass was suppressed the wrong way.
        Assert.Equal(512, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(7, options.GetProperty("seed").GetInt32());
    }

    [Fact]
    public async Task TheAssistantMessageAndTokenCountsComeBack()
    {
        var (client, _) = Build(new AiProviderOptions(), """{"language":"ar","translated":true}""");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "A report.")],
            null,
            CancellationToken.None);

        Assert.Contains("\"language\":\"ar\"", response.Text, StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(80, response.Usage?.OutputTokenCount);
    }

    [Fact]
    public async Task AFailingDaemonSurfacesRatherThanReturningEmptyText()
    {
        // The enrichment service is required to absorb provider failures, and it can only do that if
        // one is actually raised. Returning empty text would be reported as a model that answered
        // with nothing, which is a different fault with a different remedy.
        var (client, _) = Build(new AiProviderOptions(), status: HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "A report.")],
            null,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "http://localhost:11434/")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/")]
    [InlineData("http://ollama.internal:9999", "http://ollama.internal:9999/")]
    public void AnExistingOpenAiStyleEndpointStillResolves(string? configured, string expected)
    {
        // The /v1 suffix is what the shim needed. A configuration written for it must not have to be
        // edited to keep working, or upgrading would silently point this client at a path that 404s.
        var resolved = OllamaChatClient.ResolveBase(new AiProviderOptions
        {
            Provider = AiProviderKind.Ollama,
            Endpoint = configured,
        });

        Assert.Equal(expected, resolved);
    }

    private static (OllamaChatClient Client, RequestRecorder Recorder) Build(
        AiProviderOptions options,
        string content = "{}",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var recorder = new RequestRecorder(content, status);

        var httpClient = new HttpClient(recorder)
        {
            BaseAddress = new Uri(OllamaChatClient.ResolveBase(options)),
        };

        return (new OllamaChatClient(httpClient, options), recorder);
    }

    /// <summary>Captures the outbound body, which is the part these tests are actually about.</summary>
    private sealed class RequestRecorder(string content, HttpStatusCode status) : HttpMessageHandler
    {
        public JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

            var payload = JsonSerializer.Serialize(new
            {
                model = "qwen3.5:4b",
                message = new { role = "assistant", content },
                done = true,
                prompt_eval_count = 127,
                eval_count = 80,
            });

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
