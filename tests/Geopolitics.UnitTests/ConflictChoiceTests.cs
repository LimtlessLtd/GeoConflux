using System.Text.Json;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// The boundary around a model assigning reports into the register.
/// <para>
/// The rule these defend is one sentence: the model does the categorising, and it does not get to
/// decide what the categories are. Everything below is that rule stated in the four ways it can be
/// broken — answering with a key nobody offered, proposing a new conflict while also picking an
/// existing one, being asked when there was nothing to ask about, and being believed when it said it
/// was unsure.
/// </para>
/// </summary>
public sealed class ConflictChoiceTests
{
    private static readonly string[] Offered = ["ucdp:333", "ucdp:413"];

    private static string Response(string key, double confidence = 0.8, string? proposed = null) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = ConflictChoiceContract.SchemaVersion,
            conflictKey = key,
            confidence,
            rationale = "It names a party.",
            proposedName = proposed,
        });

    [Fact]
    public void AchoiceFromTheOfferIsAccepted()
    {
        var validation = ConflictChoiceValidator.Validate(Response("ucdp:413"), Offered);

        Assert.True(validation.IsValid);
        Assert.Equal("ucdp:413", validation.Value!.ConflictKey);
        Assert.Equal(0.8, validation.Value.Confidence);
    }

    [Fact]
    public void AkeyThatWasNotOfferedIsRejectedEvenThoughItLooksLikeArealOne()
    {
        // The whole design in one assertion. "ucdp:13243" is a real conflict in the real register,
        // and it was not one of the two this report was asked about — so answering with it is not
        // choosing a category, it is inventing one, and the shape of the key makes no difference.
        var validation = ConflictChoiceValidator.Validate(Response("ucdp:13243"), Offered);

        Assert.False(validation.IsValid);
        Assert.Contains("not one of the conflicts offered", validation.ErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void DecliningIsAvalidAnswer()
    {
        var validation = ConflictChoiceValidator.Validate(Response(ConflictChoiceContract.None), Offered);

        Assert.True(validation.IsValid);
        Assert.Null(validation.Value!.ConflictKey);
    }

    [Fact]
    public void ProposingAconflictWhileAlsoPickingOneIsNotAnAnswer()
    {
        var validation = ConflictChoiceValidator.Validate(
            Response("ucdp:333", proposed: "Jonglei communal fighting"),
            Offered);

        Assert.False(validation.IsValid);
        Assert.Contains("only allowed when conflictKey is NONE", validation.ErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AproposalIsCarriedOnlyWhenNothingWasChosen()
    {
        var validation = ConflictChoiceValidator.Validate(
            Response(ConflictChoiceContract.None, proposed: "Jonglei communal fighting"),
            Offered);

        Assert.True(validation.IsValid);
        Assert.Null(validation.Value!.ConflictKey);
        Assert.Equal("Jonglei communal fighting", validation.Value.ProposedName);
    }

    [Fact]
    public void TheSchemaOffersOnlyTheKeysThisRequestNamedAndTheRefusal()
    {
        var schema = ConflictChoiceContract.ResponseSchema(Offered);
        var allowed = schema
            .GetProperty("properties")
            .GetProperty("conflictKey")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();

        // Belt and braces with the validator above, deliberately. A provider that supports
        // constrained decoding cannot emit anything else; a provider that does not is caught on the
        // way back. The guarantee must not depend on which provider is configured.
        Assert.Equal(["ucdp:333", "ucdp:413", ConflictChoiceContract.None], allowed);
    }
}

/// <summary>How the pipeline uses a model's choice, and when it refuses to ask for one.</summary>
public sealed class ConflictChoicePipelineTests
{
    private static Conflict Tigray()
    {
        var conflict = Conflict.Coded("ucdp:333", "Ethiopia: Tigray", "Government of Ethiopia", "TPLF");
        conflict.RecordCoded("Mekelle town", "ET", events: 90);
        return conflict;
    }

    private static Conflict Oromiya()
    {
        var conflict = Conflict.Coded("ucdp:413", "Ethiopia: Oromiya", "Government of Ethiopia", "OLA");
        conflict.RecordCoded("Nekemte town", "ET", events: 60);
        return conflict;
    }

    private static PipelineTestHarness Ambiguous()
    {
        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(Tigray(), Oromiya()) };
        harness.LocationResolver.Behaviour = _ => new LocationResolution(
            new GeoLocation("Addis Ababa", "ET", 9.03, 38.74),
            LocationResolutionMethod.Gazetteer,
            1.0,
            null,
            null);

        return harness;
    }

    [Fact]
    public async Task AmodelIsAskedOnlyWhenTheDeterministicPassLeftAchoiceToMake()
    {
        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(Tigray()) };
        harness.ResolveAllTo(13.49, 39.47, "Mekelle");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Fighting reported.", sourceIdentifier: "x-1"),
            CancellationToken.None);

        // Geography left exactly one answer, so there was nothing to ask. Paying a model to confirm
        // a settled question is how an assignment step becomes the most expensive stage in a pipeline
        // for no gain.
        Assert.Empty(harness.ConflictClassifier.Requests);
        Assert.Single(harness.Observations.Committed[0].ConflictKeys);
    }

    [Fact]
    public async Task AmodelIsNotAskedWhenThereWasNothingToChooseBetween()
    {
        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(Tigray()) };
        harness.ResolveAllTo(-33.87, 151.21, "Sydney");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A quiet day.", sourceIdentifier: "x-2"),
            CancellationToken.None);

        // An empty offer is not a question. Asking one would invite the model to supply the list,
        // which is the single thing it must never do.
        Assert.Empty(harness.ConflictClassifier.Requests);
        Assert.Empty(harness.Observations.Committed[0].ConflictKeys);
    }

    [Fact]
    public async Task AchosenConflictIsRecordedAsAssignedRatherThanAsSomethingStronger()
    {
        var harness = Ambiguous();
        harness.ConflictClassifier.Behaviour = _ => StubConflictClassifier.Chose("ucdp:413");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Fighting was reported.", sourceIdentifier: "x-3"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);
        var request = Assert.Single(harness.ConflictClassifier.Requests);

        Assert.Equal(2, request.Options.Count);
        Assert.Equal("ucdp:413", Assert.Single(observation.ConflictKeys));

        // The basis says a model decided this. A reader weighing a membership needs to know that it
        // came from a reading of the text rather than from the source's own coding.
        Assert.Equal(ConflictMatchBasis.Assigned, observation.ConflictBasis);

        // And the one it did not pick stays visible as the alternative it was.
        Assert.Equal("ucdp:333", Assert.Single(observation.ConflictCandidateKeys));
    }

    [Fact]
    public async Task AnUnsureModelIsRecordedAndNotBelieved()
    {
        var harness = Ambiguous();
        harness.ConflictClassifier.Behaviour = _ => StubConflictClassifier.Chose("ucdp:413", confidence: 0.1);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Fighting was reported.", sourceIdentifier: "x-4"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        Assert.Empty(observation.ConflictKeys);
        Assert.Equal(2, observation.ConflictCandidateKeys.Count);

        // Recorded whether or not it was believed, which is what makes the threshold auditable
        // rather than a silent filter.
        var inference = Assert.Single(harness.Inferences.Committed);
        Assert.Equal(0.1, inference.Confidence);
        Assert.Equal(ConflictChoicePrompt.Version, inference.PromptVersion);
    }

    [Fact]
    public async Task AproposedConflictIsRecordedAsAclaimAndChangesNothing()
    {
        var harness = Ambiguous();
        harness.ConflictClassifier.Behaviour = _ => StubConflictClassifier.Declined("Jonglei communal fighting");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Fighting was reported.", sourceIdentifier: "x-5"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        Assert.Empty(observation.ConflictKeys);
        Assert.Contains("Jonglei communal fighting", observation.ConflictNote, StringComparison.Ordinal);
        Assert.Contains("nothing has been added to the register", observation.ConflictNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmodelFailingCostsAlabelAndNothingElse()
    {
        var harness = Ambiguous();
        harness.ConflictClassifier.Behaviour = _ => StubConflictClassifier.Failed();

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Fighting was reported.", sourceIdentifier: "x-6"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        Assert.Empty(observation.ConflictKeys);
        Assert.Equal(ObservationStatus.Persisted, observation.Status);
        Assert.NotNull(observation.IncidentId);

        var inference = Assert.Single(harness.Inferences.Committed);
        Assert.Equal(AiInferenceOutcome.ProviderFailed, inference.Outcome);
    }
}
