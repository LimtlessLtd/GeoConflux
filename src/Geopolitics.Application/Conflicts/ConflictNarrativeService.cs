using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;
using Microsoft.Extensions.Options;

namespace Geopolitics.Application.Conflicts;

public interface IConflictNarrativeService
{
    Task<ConflictNarrative?> BuildAsync(
        string conflictKey,
        AnalyticsWindow window,
        CancellationToken cancellationToken);
}

/// <summary>
/// A per-conflict summary, or a refusal to write one.
/// <para>
/// The floor is the point of this class. A model handed three reports writes a paragraph as fluent
/// and as confident as one handed three hundred, and prose carries no field beside it that a reader
/// checks the way they check a confidence score on a category. So whether there is enough to
/// characterise is decided here, from the evidence, before any model is asked — and the refusal is
/// written by this system, in plain terms, and labelled as not model-written.
/// </para>
/// <para>
/// Two conditions, and the second is the one that is easy to leave out. Enough reports, <em>and</em>
/// enough sources: forty reports that are one outlet's coverage of one week is one account of a war,
/// however many rows it occupies, and a summary of it would read as a picture of the conflict.
/// </para>
/// </summary>
public sealed class ConflictNarrativeService(
    IConflictActivityRepository repository,
    IConflictRegister register,
    IConflictNarrator narrator,
    IOptions<EnrichmentOptions> enrichmentOptions,
    TimeProvider timeProvider) : IConflictNarrativeService
{
    /// <summary>
    /// Fewest reports that can be characterised at all. Below this the honest output is the count.
    /// </summary>
    public const int MinimumReports = 5;

    /// <summary>
    /// Fewest distinct sources. One source's reporting is that source's account, and summarising it
    /// as though it were the conflict is the failure a corroboration rule exists to prevent
    /// everywhere else in this system.
    /// </summary>
    public const int MinimumSources = 2;

    private readonly EnrichmentOptions enrichment = enrichmentOptions.Value;

    public async Task<ConflictNarrative?> BuildAsync(
        string conflictKey,
        AnalyticsWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!register.TryGet(conflictKey, out var conflict))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var evidence = await repository.EvidenceAsync(
            conflict.Key,
            now - window.Duration,
            now,
            ConflictNarrativePrompt.MaxReports,
            cancellationToken);

        var sources = evidence.Select(item => item.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (evidence.Count < MinimumReports)
        {
            return ConflictNarrative.Declined(
                conflict.Key,
                conflict.Name,
                window.Token,
                evidence.Count == 0
                    ? $"Nothing reached this system about this conflict in the {window.Label.ToLowerInvariant()}."
                    : $"{Reports(evidence.Count)} in the {window.Label.ToLowerInvariant()}; too little to "
                      + "characterise.",
                evidence.Count,
                sources);
        }

        if (sources < MinimumSources)
        {
            return ConflictNarrative.Declined(
                conflict.Key,
                conflict.Name,
                window.Token,
                $"{Reports(evidence.Count)} in the {window.Label.ToLowerInvariant()}, all from one source. "
                + "That is one account of this conflict rather than a picture of it, so it is shown as "
                + "reports rather than summarised.",
                evidence.Count,
                sources);
        }

        var result = await narrator.WriteAsync(
            new ConflictNarrativeRequest(conflict.Name, window.Label, evidence),
            cancellationToken);

        if (result.Narrative is not { } written)
        {
            return ConflictNarrative.Declined(
                conflict.Key,
                conflict.Name,
                window.Token,
                $"{Reports(evidence.Count)} from {sources} sources. No summary was produced: "
                + (result.Outcome == AiInferenceOutcome.Skipped
                    ? "no model is configured."
                    : "the model did not answer usefully."),
                evidence.Count,
                sources);
        }

        if (written.Confidence < enrichment.MinimumAcceptedConfidence)
        {
            // The model wrote something and said it was unsure of it. Showing the paragraph with the
            // number beside it does not help: a reader takes in the prose and not the decimal, which
            // is the whole reason prose is the dangerous artefact here.
            return ConflictNarrative.Declined(
                conflict.Key,
                conflict.Name,
                window.Token,
                $"{Reports(evidence.Count)} from {sources} sources. A summary was written and withheld: "
                + "the model reported it was not confident these reports support it.",
                evidence.Count,
                sources);
        }

        return new ConflictNarrative(
            conflict.Key,
            conflict.Name,
            window.Token,
            written.Summary,
            IsModelWritten: true,
            evidence.Count,
            sources,
            written.Confidence,
            $"{result.Provider}/{result.Model}",
            result.PromptVersion);
    }

    private static string Reports(int count) => count == 1 ? "One report" : $"{count} reports";
}
