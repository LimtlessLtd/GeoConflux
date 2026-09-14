using System.Diagnostics;
using System.Text.Json;
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
/// Asks a model to choose between conflicts, over any <see cref="IChatClient"/>.
/// <para>
/// Structurally the same as <see cref="ChatClientEnrichmentService"/> and deliberately so: constrain
/// the request where the provider supports it, validate what comes back, allow exactly one repair
/// turn, and turn every remaining failure into a reported outcome rather than an exception. It shares
/// the enrichment timeout and repair budget because it is the same provider under the same operating
/// conditions, and a second set of knobs would be two things to tune and one of them forgotten.
/// </para>
/// <para>
/// What it does not share is the ability to matter. An unassigned report is an ordinary outcome that
/// the system produces all day without any model at all, so this failing costs a label and nothing
/// else.
/// </para>
/// </summary>
public sealed partial class ChatClientConflictClassifier(
    IChatClient chatClient,
    IOptions<EnrichmentOptions> enrichmentOptions,
    IOptions<AiProviderOptions> providerOptions,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<ChatClientConflictClassifier> logger) : IConflictClassifier
{
    private readonly EnrichmentOptions enrichment = enrichmentOptions.Value;
    private readonly AiProviderOptions provider = providerOptions.Value;

    public async Task<ConflictChoiceResult> ChooseAsync(
        ConflictChoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!enrichment.Enabled)
        {
            return ConflictChoiceResult.Skipped("Enrichment is disabled by configuration.");
        }

        if (request.Options.Count == 0)
        {
            // Not a question. Asking a model to pick from an empty list invites it to supply the
            // list, which is the one thing it must never do.
            return ConflictChoiceResult.Skipped("No conflict was offered, so there was nothing to choose between.");
        }

        var offered = request.Options.Take(ConflictChoiceContract.MaxOptions).ToArray();
        var keys = offered.Select(option => option.Key).ToArray();
        var providerName = provider.Provider.ToString();
        var startedAt = timeProvider.GetTimestamp();

        using var activity = diagnostics.ActivitySource.StartActivity("ai.assign_conflict", ActivityKind.Client);
        activity?.SetTag("ai.provider", providerName);
        activity?.SetTag("ai.model", provider.Model);
        activity?.SetTag("ai.prompt_version", ConflictChoicePrompt.Version);
        activity?.SetTag("conflict.options", offered.Length);

        using var timeoutSource = new CancellationTokenSource(enrichment.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ConflictChoicePrompt.SystemInstruction),
            new(ChatRole.User, ConflictChoicePrompt.BuildUserMessage(
                request.SourceName,
                request.Title,
                request.Content,
                offered)),
        };

        var options = BuildChatOptions(keys);
        var attempts = 0;
        IReadOnlyList<string> lastErrors = ["The classifier produced no result."];

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
                var validation = ConflictChoiceValidator.Validate(response.Text, keys);

                if (validation.Value is { } accepted)
                {
                    var elapsed = Elapsed(startedAt);
                    diagnostics.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("provider", providerName));
                    activity?.SetTag("ai.attempts", attempts);
                    activity?.SetTag("ai.confidence", accepted.Confidence);
                    activity?.SetTag("conflict.chosen", accepted.ConflictKey ?? "none");

                    return new ConflictChoiceResult(
                        accepted,
                        AiInferenceOutcome.Succeeded,
                        providerName,
                        response.ModelId ?? provider.Model,
                        ConflictChoicePrompt.Version,
                        ConflictChoiceContract.SchemaVersion,
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

                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                messages.Add(new ChatMessage(ChatRole.User, ConflictChoicePrompt.BuildRepairMessage(validation.Errors)));
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

            return Failure(
                AiInferenceOutcome.ProviderFailed,
                providerName,
                attempts,
                Elapsed(startedAt),
                $"The provider call failed ({exception.GetType().Name}).",
                activity);
        }
    }

    private ChatOptions BuildChatOptions(IReadOnlyList<string> keys) => new()
    {
        ModelId = string.IsNullOrWhiteSpace(provider.Model) ? null : provider.Model,
        Temperature = provider.Temperature,
        Seed = provider.Seed,
        MaxOutputTokens = provider.MaxOutputTokens,

        // The schema is built per request with this request's keys as the only permitted answers, so
        // a provider that supports constrained decoding cannot emit a conflict that was not offered.
        // Providers without it are caught by the validator, which applies the identical rule.
        ResponseFormat = ChatResponseFormat.ForJsonSchema(
            ConflictChoiceContract.ResponseSchema(keys),
            "conflict_assignment",
            "Which known conflict one report belongs to."),
    };

    /// <summary>
    /// Serialises the validated choice rather than the provider's raw text, so a stored inference
    /// cannot end up holding deliberation or a field the model decided to add.
    /// </summary>
    private static string Serialise(ConflictChoice value) => JsonSerializer.Serialize(
        new
        {
            schemaVersion = ConflictChoiceContract.SchemaVersion,
            conflictKey = value.ConflictKey ?? ConflictChoiceContract.None,
            confidence = value.Confidence,
            rationale = value.Rationale,
            proposedName = value.ProposedName,
        },
        ConflictChoiceContract.SerializerOptions);

    private ConflictChoiceResult Failure(
        AiInferenceOutcome outcome,
        string providerName,
        int attempts,
        double elapsed,
        string error,
        Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error);
        diagnostics.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("provider", providerName));

        return new ConflictChoiceResult(
            null,
            outcome,
            providerName,
            provider.Model,
            ConflictChoicePrompt.Version,
            ConflictChoiceContract.SchemaVersion,
            attempts,
            elapsed,
            StructuredOutput: null,
            error);
    }

    private double Elapsed(long startedAt) => timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conflict assignment output from {Provider} failed validation on attempt {Attempt}: {Errors}")]
    private static partial void LogValidationFailed(ILogger logger, string provider, int attempt, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conflict assignment provider {Provider} did not respond within {TimeoutSeconds}s.")]
    private static partial void LogTimedOut(ILogger logger, string provider, double timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conflict assignment provider {Provider} failed; the report is left unassigned.")]
    private static partial void LogProviderFailed(ILogger logger, Exception exception, string provider);
}
