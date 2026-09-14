using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// A conflict classifier that answers whatever a test tells it to.
/// <para>
/// Skipped by default, which is the state of a deployment with no model configured and the state
/// almost every test wants: the deterministic assignment stands on its own, and nothing in these
/// tests is quietly depending on a stand-in's opinion.
/// </para>
/// </summary>
public sealed class StubConflictClassifier : IConflictClassifier
{
    /// <summary>What the classifier does with a request. Replaced by tests that care.</summary>
    public Func<ConflictChoiceRequest, ConflictChoiceResult> Behaviour { get; set; } =
        _ => ConflictChoiceResult.Skipped("No model is configured.");

    /// <summary>Requests the pipeline actually made, so a test can assert it was not asked.</summary>
    public List<ConflictChoiceRequest> Requests { get; } = [];

    public Task<ConflictChoiceResult> ChooseAsync(
        ConflictChoiceRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Behaviour(request));
    }

    public static ConflictChoiceResult Chose(string conflictKey, double confidence = 0.8) => new(
        new ConflictChoice(conflictKey, confidence, "It names a party to that conflict.", null),
        AiInferenceOutcome.Succeeded,
        "stub",
        "stub-model",
        ConflictChoicePrompt.Version,
        ConflictChoiceContract.SchemaVersion,
        Attempts: 1,
        LatencyMilliseconds: 1,
        StructuredOutput: "{}",
        Error: null);

    public static ConflictChoiceResult Declined(string? proposedName = null) => new(
        new ConflictChoice(null, 0.9, "None of these fits.", proposedName),
        AiInferenceOutcome.Succeeded,
        "stub",
        "stub-model",
        ConflictChoicePrompt.Version,
        ConflictChoiceContract.SchemaVersion,
        Attempts: 1,
        LatencyMilliseconds: 1,
        StructuredOutput: "{}",
        Error: null);

    public static ConflictChoiceResult Failed() => new(
        null,
        AiInferenceOutcome.ProviderFailed,
        "stub",
        "stub-model",
        ConflictChoicePrompt.Version,
        ConflictChoiceContract.SchemaVersion,
        Attempts: 1,
        LatencyMilliseconds: 1,
        StructuredOutput: null,
        Error: "The provider call failed.");
}
