using System.Diagnostics;
using System.Text.Json;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Ai;

/// <summary>
/// Enrichment over any <see cref="IChatClient"/>.
/// <para>
/// The provider is injected rather than chosen here, so switching between the offline stand-in, a
/// local Ollama daemon, OpenAI, and Azure OpenAI is a configuration change and this class does not
/// know which it is talking to.
/// </para>
/// <para>
/// Its real job is the trust boundary around the response: constrain the request where the provider
/// supports it, validate what comes back, give the model exactly one chance to correct itself
/// against the validation errors, and turn every remaining failure into a reported outcome rather
/// than an exception. The pipeline is expected to carry on without this service, so nothing here may
/// be allowed to cost an observation.
/// </para>
/// </summary>
public sealed partial class ChatClientEnrichmentService(
    IChatClient chatClient,
    IOptions<EnrichmentOptions> enrichmentOptions,
    IOptions<AiProviderOptions> providerOptions,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<ChatClientEnrichmentService> logger) : IEventEnrichmentService
{
    private readonly EnrichmentOptions enrichment = enrichmentOptions.Value;
    private readonly AiProviderOptions provider = providerOptions.Value;

    public async Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!enrichment.Enabled)
        {
            return EnrichmentResult.Skipped("Enrichment is disabled by configuration.");
        }

        var providerName = provider.Provider.ToString();
        var startedAt = timeProvider.GetTimestamp();

        using var activity = diagnostics.ActivitySource.StartActivity("ai.enrich", ActivityKind.Client);
        activity?.SetTag("ai.provider", providerName);
        activity?.SetTag("ai.model", provider.Model);
        activity?.SetTag("ai.prompt_version", EnrichmentPrompt.Version);
        activity?.SetTag("ai.schema_version", EnrichmentContract.SchemaVersion);

        // A timeout the caller did not ask for must not look like caller cancellation, so the two
        // tokens stay separately identifiable below.
        using var timeoutSource = new CancellationTokenSource(enrichment.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, EnrichmentPrompt.SystemInstruction),
            new(ChatRole.User, EnrichmentPrompt.BuildUserMessage(request.SourceName, request.Title, request.Content)),
        };

        var options = BuildChatOptions();
        var attempts = 0;
        IReadOnlyList<string> lastErrors = ["Enrichment produced no result."];

        try
        {
            while (attempts <= enrichment.MaxRepairAttempts)
            {
                attempts++;
                diagnostics.AiRequests.Add(1, new KeyValuePair<string, object?>("provider", providerName));

                if (attempts > 1)
                {
                    diagnostics.AiRepairAttempts.Add(1, new KeyValuePair<string, object?>("provider", providerName));
                }

                var response = await chatClient.GetResponseAsync(messages, options, linked.Token);
                var validation = EnrichmentPayloadValidator.Validate(response.Text);

                if (validation.Value is { } accepted)
                {
                    var elapsed = Elapsed(startedAt);
                    diagnostics.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("provider", providerName));
                    activity?.SetTag("ai.attempts", attempts);
                    activity?.SetTag("ai.confidence", accepted.Confidence);

                    return new EnrichmentResult(
                        accepted,
                        AiInferenceOutcome.Succeeded,
                        providerName,
                        response.ModelId ?? provider.Model,
                        EnrichmentPrompt.Version,
                        EnrichmentContract.SchemaVersion,
                        attempts,
                        elapsed,
                        Serialise(accepted),
                        Error: null);
                }

                lastErrors = validation.Errors;
                diagnostics.AiValidationFailures.Add(1, new KeyValuePair<string, object?>("provider", providerName));
                LogValidationFailed(logger, providerName, attempts, validation.ErrorSummary);

                if (attempts > enrichment.MaxRepairAttempts)
                {
                    break;
                }

                // The rejected answer stays in the conversation. Without it the model is being asked
                // to correct something it can no longer see.
                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                messages.Add(new ChatMessage(ChatRole.User, EnrichmentPrompt.BuildRepairMessage(validation.Errors)));
            }

            return Failure(
                AiInferenceOutcome.ValidationFailed,
                providerName,
                attempts,
                Elapsed(startedAt),
                $"Output failed schema validation after {attempts} attempt(s): {string.Join(" ", lastErrors)}",
                activity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. Not an enrichment failure, and not this service's to absorb.
            throw;
        }
        catch (OperationCanceledException)
        {
            diagnostics.AiFailures.Add(1, new KeyValuePair<string, object?>("reason", "timeout"));
            LogTimedOut(logger, providerName, enrichment.Timeout.TotalSeconds);

            return Failure(
                AiInferenceOutcome.ProviderFailed,
                providerName,
                attempts,
                Elapsed(startedAt),
                $"The provider did not respond within {enrichment.Timeout.TotalSeconds:F0}s.",
                activity);
        }
        catch (Exception exception)
        {
            diagnostics.AiFailures.Add(1, new KeyValuePair<string, object?>("reason", "provider"));
            LogProviderFailed(logger, exception, providerName);

            // The exception message can carry provider detail, so only its type is recorded. The
            // full exception goes to the log, which is not a user-facing surface.
            return Failure(
                AiInferenceOutcome.ProviderFailed,
                providerName,
                attempts,
                Elapsed(startedAt),
                $"The provider call failed ({exception.GetType().Name}).",
                activity);
        }
    }

    private ChatOptions BuildChatOptions() => new()
    {
        ModelId = string.IsNullOrWhiteSpace(provider.Model) ? null : provider.Model,
        Temperature = provider.Temperature,
        Seed = provider.Seed,
        MaxOutputTokens = provider.MaxOutputTokens,

        // Providers that support constrained decoding will honour this and rarely produce anything
        // invalid. Those that do not are caught by the validator instead, which is why the guarantee
        // does not depend on the provider having the feature.
        ResponseFormat = ChatResponseFormat.ForJsonSchema(
            EnrichmentContract.ResponseSchema,
            "geopolitical_enrichment",
            "Structured enrichment of one geopolitical observation."),
    };

    /// <summary>
    /// Serialises the <em>validated</em> projection rather than the provider's raw text. Only fields
    /// the contract defines survive this, so an inference record cannot end up holding deliberation,
    /// commentary, or any extra field a model decided to add.
    /// </summary>
    private static string Serialise(ValidatedEnrichment value) => JsonSerializer.Serialize(
        new
        {
            schemaVersion = EnrichmentContract.SchemaVersion,
            language = value.Language,
            summary = value.Summary,
            eventType = EnrichmentContract.ToWire(value.EventType),
            severity = EnrichmentContract.ToWire(value.Severity),
            confidence = value.Confidence,
            severityRationale = value.SeverityRationale,
            locationName = value.LocationName,
            entities = value.Entities.Select(entity => new
            {
                name = entity.Name,
                type = EnrichmentContract.ToWire(entity.Type),
            }),
        },
        EnrichmentContract.SerializerOptions);

    private EnrichmentResult Failure(
        AiInferenceOutcome outcome,
        string providerName,
        int attempts,
        double elapsed,
        string error,
        Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error);
        diagnostics.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("provider", providerName));

        return new EnrichmentResult(
            null,
            outcome,
            providerName,
            provider.Model,
            EnrichmentPrompt.Version,
            EnrichmentContract.SchemaVersion,
            attempts,
            elapsed,
            StructuredOutput: null,
            error);
    }

    private double Elapsed(long startedAt) => timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrichment output from {Provider} failed validation on attempt {Attempt}: {Errors}")]
    private static partial void LogValidationFailed(ILogger logger, string provider, int attempt, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrichment provider {Provider} did not respond within {TimeoutSeconds}s.")]
    private static partial void LogTimedOut(ILogger logger, string provider, double timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrichment provider {Provider} failed; the deterministic classifier will be used instead.")]
    private static partial void LogProviderFailed(ILogger logger, Exception exception, string provider);
}
