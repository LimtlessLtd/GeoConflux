using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <param name="ConflictName">The conflict as the coding names it.</param>
/// <param name="Window">The window being characterised, as a label.</param>
/// <param name="Evidence">The reports to summarise. Excerpts, never whole articles.</param>
public sealed record ConflictNarrativeRequest(
    string ConflictName,
    string Window,
    IReadOnlyList<ConflictEvidence> Evidence);

/// <param name="Narrative">The validated summary, or <see langword="null"/> when nothing usable came back.</param>
public sealed record ConflictNarrativeResult(
    ConflictNarrativeText? Narrative,
    AiInferenceOutcome Outcome,
    string Provider,
    string Model,
    string PromptVersion,
    int SchemaVersion,
    int Attempts,
    double LatencyMilliseconds,
    string? Error)
{
    public bool IsSuccess => Narrative is not null && Outcome == AiInferenceOutcome.Succeeded;

    public static ConflictNarrativeResult Skipped(string reason) => new(
        null,
        AiInferenceOutcome.Skipped,
        "none",
        string.Empty,
        ConflictNarrativePrompt.Version,
        ConflictNarrativeContract.SchemaVersion,
        Attempts: 0,
        LatencyMilliseconds: 0,
        Error: reason);
}

/// <summary>
/// Writes what a window's reports about one conflict say.
/// <para>
/// The largest thing missing from this dashboard and the one job a model is genuinely better at than
/// a heuristic. It is also the most dangerous artefact available here, because a model handed three
/// reports writes a confident paragraph in exactly the same voice as one handed three hundred — and
/// unlike a category, prose carries no field a reader can check the confidence of.
/// </para>
/// <para>
/// The evidence floor is therefore applied by the caller, before this is ever reached. Whether there
/// is enough to characterise is a property of the evidence and not a judgement the model should be
/// trusted to make about its own output.
/// </para>
/// </summary>
public interface IConflictNarrator
{
    Task<ConflictNarrativeResult> WriteAsync(ConflictNarrativeRequest request, CancellationToken cancellationToken);
}
