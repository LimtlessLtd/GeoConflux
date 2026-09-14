using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <param name="Key">The register key the model must answer with if it chooses this one.</param>
/// <param name="Name">The conflict as the coding names it.</param>
/// <param name="SideA">A party, as the coding names it. Null where it names none.</param>
/// <param name="Where">Countries it is coded in, as a short readable list.</param>
public sealed record ConflictOption(
    string Key,
    string Name,
    string? SideA,
    string? SideB,
    string? Where);

/// <param name="SourceName">Who reported it, so the model can weigh the framing.</param>
/// <param name="Options">The conflicts it may choose between. Never empty; an empty offer is not a question.</param>
public sealed record ConflictChoiceRequest(
    string SourceName,
    string? Title,
    string Content,
    IReadOnlyList<ConflictOption> Options);

/// <param name="Choice">The validated answer, or <see langword="null"/> when nothing usable came back.</param>
/// <param name="Outcome">Whether the attempt succeeded, failed validation, failed at the provider, or was skipped.</param>
public sealed record ConflictChoiceResult(
    ConflictChoice? Choice,
    AiInferenceOutcome Outcome,
    string Provider,
    string Model,
    string PromptVersion,
    int SchemaVersion,
    int Attempts,
    double LatencyMilliseconds,
    string? StructuredOutput,
    string? Error)
{
    public bool IsSuccess => Choice is not null && Outcome == AiInferenceOutcome.Succeeded;

    public static ConflictChoiceResult Skipped(string reason) => new(
        null,
        AiInferenceOutcome.Skipped,
        "none",
        string.Empty,
        ConflictChoicePrompt.Version,
        ConflictChoiceContract.SchemaVersion,
        Attempts: 0,
        LatencyMilliseconds: 0,
        StructuredOutput: null,
        Error: reason);
}

/// <summary>
/// Asks a model which of several known conflicts a report belongs to.
/// <para>
/// Separate from enrichment rather than folded into it, and the ordering is why. The conflicts worth
/// offering depend on where the report was placed, and placement depends on the place name enrichment
/// extracted — so this question cannot be asked until enrichment has already answered. It is also
/// asked far less often: the deterministic pass settles most reports, and this runs only where it
/// genuinely could not.
/// </para>
/// <para>
/// Implementations must not throw for provider failures, for the same reason enrichment must not. A
/// model being unreachable is an operating condition, and an unassigned report is a perfectly
/// acceptable outcome — it is what the system already does without a model at all.
/// </para>
/// </summary>
public interface IConflictClassifier
{
    Task<ConflictChoiceResult> ChooseAsync(ConflictChoiceRequest request, CancellationToken cancellationToken);
}
