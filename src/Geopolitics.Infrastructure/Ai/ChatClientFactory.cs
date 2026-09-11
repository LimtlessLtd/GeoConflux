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
/// Three of the four providers share one SDK. Ollama exposes an OpenAI-compatible endpoint, so
/// pointing the OpenAI client at the local daemon covers local development without a second client
/// library to keep current; Azure OpenAI needs its own client because it authenticates and routes
/// differently, and pretending otherwise would make the Azure option a claim rather than a feature.
/// </para>
/// </summary>
public static class ChatClientFactory
{
    /// <summary>Activity source for GenAI spans, registered with the OpenTelemetry tracer provider.</summary>
    public const string ActivitySourceName = "Geopolitics.Ai";

    /// <summary>
    /// Ollama ignores the bearer token entirely, but the OpenAI client requires a non-empty
    /// credential to construct. This placeholder is not a secret and grants nothing.
    /// </summary>
    private const string OllamaPlaceholderCredential = "ollama-local";

    public static IChatClient Create(AiProviderOptions options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        var inner = options.Provider switch
        {
            AiProviderKind.Mock => services.GetRequiredService<DeterministicMockChatClient>(),
            AiProviderKind.Ollama => CreateOpenAICompatible(options, OllamaPlaceholderCredential),
            AiProviderKind.OpenAI => CreateOpenAICompatible(options, RequireApiKey(options, "OpenAI")),
            AiProviderKind.AzureOpenAI => CreateAzure(options),
            _ => throw new InvalidOperationException($"Unsupported AI provider '{options.Provider}'."),
        };

        return inner
            .AsBuilder()
            .UseOpenTelemetry(loggerFactory, ActivitySourceName)
            .Build(services);
    }

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
