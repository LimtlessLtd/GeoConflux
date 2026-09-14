using System.Diagnostics;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Conflicts;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Ai;

/// <summary>
/// Writes a per-conflict summary over any <see cref="IChatClient"/>.
/// <para>
/// The same trust plumbing as enrichment and assignment: constrain the request, validate the answer,
/// allow one repair turn, and turn every failure into a reported outcome. What differs is what
/// happens when it fails — nothing. A conflict with no summary shows its reports, which is what the
/// page did before there were summaries at all.
/// </para>
/// </summary>
public sealed partial class ChatClientConflictNarrator(
    IChatClient chatClient,
    IOptions<EnrichmentOptions> enrichmentOptions,
    IOptions<AiProviderOptions> providerOptions,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<ChatClientConflictNarrator> logger) : IConflictNarrator
{
    private readonly EnrichmentOptions enrichment = enrichmentOptions.Value;
    private readonly AiProviderOptions provider = providerOptions.Value;

    public async Task<ConflictNarrativeResult> WriteAsync(
        ConflictNarrativeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!enrichment.Enabled)
        {
            return ConflictNarrativeResult.Skipped("Enrichment is disabled by configuration.");
        }

        if (request.Evidence.Count == 0)
        {
            return ConflictNarrativeResult.Skipped("There was no evidence to summarise.");
        }

        var providerName = provider.Provider.ToString();
        var startedAt = timeProvider.GetTimestamp();

        using var activity = diagnostics.ActivitySource.StartActivity("ai.narrate_conflict", ActivityKind.Client);
        activity?.SetTag("ai.provider", providerName);
        activity?.SetTag("ai.prompt_version", ConflictNarrativePrompt.Version);
        activity?.SetTag("conflict.reports", request.Evidence.Count);

        using var timeoutSource = new CancellationTokenSource(enrichment.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ConflictNarrativePrompt.SystemInstruction),
            new(ChatRole.User, ConflictNarrativePrompt.BuildUserMessage(
                request.ConflictName,
                request.Window,
                request.Evidence)),
        };

        var options = BuildChatOptions();
        var attempts = 0;
        IReadOnlyList<string> lastErrors = ["The narrator produced no result."];

        try
        {
            while (attempts <= enrichment.MaxRepairAttempts)
            {
                attempts++;
                diagnostics.AiRequests.Add(1, new KeyValuePair<string, object?>("provider", providerName));

                var response = await chatClient.GetResponseAsync(messages, options, linked.Token);
                var validation = ConflictNarrativeValidator.Validate(response.Text);

                if (validation.Value is { } accepted)
                {
                    var elapsed = Elapsed(startedAt);
                    diagnostics.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("provider", providerName));
                    activity?.SetTag("ai.confidence", accepted.Confidence);

                    return new ConflictNarrativeResult(
                        accepted,
                        AiInferenceOutcome.Succeeded,
                        providerName,
                        response.ModelId ?? provider.Model,
                        ConflictNarrativePrompt.Version,
                        ConflictNarrativeContract.SchemaVersion,
                        attempts,
                        elapsed,
                        Error: null);
                }

                lastErrors = validation.Errors;
                diagnostics.AiValidationFailures.Add(1, new KeyValuePair<string, object?>("provider", providerName));
                LogValidationFailed(logger, providerName, attempts, validation.ErrorSummary);

                if (attempts > enrichment.MaxRepairAttempts)
                {
                    break;
                }

                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                messages.Add(new ChatMessage(ChatRole.User, ConflictNarrativePrompt.BuildRepairMessage(validation.Errors)));
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
            throw;
        }
        catch (OperationCanceledException)
        {
            diagnostics.AiFailures.Add(1, new KeyValuePair<string, object?>("reason", "timeout"));

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
        ResponseFormat = ChatResponseFormat.ForJsonSchema(
            ConflictNarrativeContract.ResponseSchema,
            "conflict_narrative",
            "What one window of reports about one conflict says."),
    };

    private ConflictNarrativeResult Failure(
        AiInferenceOutcome outcome,
        string providerName,
        int attempts,
        double elapsed,
        string error,
        Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error);
        diagnostics.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("provider", providerName));

        return new ConflictNarrativeResult(
            null,
            outcome,
            providerName,
            provider.Model,
            ConflictNarrativePrompt.Version,
            ConflictNarrativeContract.SchemaVersion,
            attempts,
            elapsed,
            error);
    }

    private double Elapsed(long startedAt) => timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conflict narrative output from {Provider} failed validation on attempt {Attempt}: {Errors}")]
    private static partial void LogValidationFailed(ILogger logger, string provider, int attempt, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conflict narrative provider {Provider} failed; the conflict's reports are shown without a summary.")]
    private static partial void LogProviderFailed(ILogger logger, Exception exception, string provider);
}
