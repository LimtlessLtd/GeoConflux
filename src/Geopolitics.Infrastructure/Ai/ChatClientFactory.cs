using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Geopolitics.Infrastructure.Ai;

/// <summary>
/// Builds the configured <see cref="IChatClient"/>.
/// <para>
/// OpenAI and Azure OpenAI share one SDK; Azure needs its own client because it authenticates and
/// routes differently, and pretending otherwise would make the Azure option a claim rather than a
/// feature. Ollama used to be pointed at the same OpenAI client through its compatibility shim, and
/// now has its own: the shim cannot turn off a local model's reasoning pass, which costs an order of
/// magnitude on this workload. See <see cref="OllamaChatClient"/>.
/// </para>
/// </summary>
public static class ChatClientFactory
{
    /// <summary>Activity source for GenAI spans, registered with the OpenTelemetry tracer provider.</summary>
    public const string ActivitySourceName = "Geopolitics.Ai";


    public static IChatClient Create(AiProviderOptions options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        var inner = options.Provider switch
        {
            AiProviderKind.Mock => services.GetRequiredService<DeterministicMockChatClient>(),
            AiProviderKind.Ollama => CreateOllama(options, services),
            AiProviderKind.OpenAI => CreateOpenAICompatible(options, RequireApiKey(options, "OpenAI")),
            AiProviderKind.AzureOpenAI => CreateAzure(options),
            _ => throw new InvalidOperationException($"Unsupported AI provider '{options.Provider}'."),
        };

        return inner
            .AsBuilder()
            .UseOpenTelemetry(loggerFactory, ActivitySourceName)
            .Build(services);
    }

    /// <summary>
    /// Builds the local client over a plain <see cref="HttpClient"/>. Deliberately not the screened
    /// handler the OSINT adapters use: that one refuses to connect to a private address, which is
    /// exactly what a daemon on localhost is.
    /// </summary>
    private static OllamaChatClient CreateOllama(AiProviderOptions options, IServiceProvider services)
    {
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaClientName);
        client.BaseAddress = new Uri(OllamaChatClient.ResolveBase(options));
        return new OllamaChatClient(client, options);
    }

    /// <summary>Named client for the local daemon, registered without the public-internet screen.</summary>
    public const string OllamaClientName = "ollama";

    private static IChatClient CreateOpenAICompatible(AiProviderOptions options, string apiKey)
    {
        var clientOptions = new OpenAIClientOptions();

        if (options.ResolveEndpoint() is { } endpoint)
        {
            clientOptions.Endpoint = new Uri(endpoint);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    private static IChatClient CreateAzure(AiProviderOptions options)
    {
        var endpoint = options.ResolveEndpoint()
            ?? throw new InvalidOperationException("Ai:Endpoint is required when the provider is AzureOpenAI.");

        return new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(RequireApiKey(options, "AzureOpenAI")))
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    /// <summary>
    /// Fails at composition rather than on the first observation. A provider selected without a
    /// credential is a deployment mistake, and discovering it at startup is far cheaper than
    /// discovering it as a stream of silently degraded enrichments.
    /// </summary>
    private static string RequireApiKey(AiProviderOptions options, string providerName) =>
        string.IsNullOrWhiteSpace(options.ApiKey)
            ? throw new InvalidOperationException(
                $"Ai:ApiKey is required when the provider is {providerName}. Supply it through environment "
                + "variables, user secrets, or a secret store — never in a committed file.")
            : options.ApiKey;
}
