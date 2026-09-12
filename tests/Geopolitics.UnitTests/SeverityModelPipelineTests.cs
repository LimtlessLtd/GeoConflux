using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// Covers what the severity model is allowed to do inside the pipeline, which is almost nothing.
/// <para>
/// The tests worth having here are the negative ones. A second opinion that can change a stored
/// severity is not a second opinion, and one that can fail the processing of an observation is a new
/// dependency rather than an addition — so those two properties are asserted directly rather than
/// left to the reading of the code.
/// </para>
/// </summary>
public sealed class SeverityModelPipelineTests
{
    [Fact]
    public async Task ThePredictionIsRecordedAlongsideTheSeverityThePipelineApplied()
    {
        var harness = new PipelineTestHarness
        {
            SeverityModel = new ScriptedSeverityModel(Severity.Critical, 0.82, "test-model/v3"),
        };

        var processor = harness.BuildProcessor();

        await processor.ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "Naval escort activity was recorded in the Red Sea. No engagement took place.",
                DeclaredSeverity = Severity.Low,
            },
            CancellationToken.None);

        var stored = Assert.Single(harness.Observations.Committed);

        Assert.Equal(Severity.Critical, stored.ModelSeverity);
        Assert.Equal(0.82, stored.ModelSeverityConfidence);
        Assert.Equal("test-model/v3", stored.ModelVersion);

        // The source declared Low and that is what was applied. The model said Critical and that is
        // recorded as a disagreement, not as an override.
        Assert.Equal(Severity.Low, stored.Severity);
        Assert.True(stored.ModelDisagrees);
    }

    [Fact]
    public async Task AgreementIsNotReportedAsDisagreement()
    {
        var harness = new PipelineTestHarness
        {
            SeverityModel = new ScriptedSeverityModel(Severity.Low, 0.6),
        };

        await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "A patrol reported a quiet night along the contact line.",
                DeclaredSeverity = Severity.Low,
            },
            CancellationToken.None);

        var stored = Assert.Single(harness.Observations.Committed);

        Assert.Equal(Severity.Low, stored.ModelSeverity);
        Assert.False(stored.ModelDisagrees);
    }

    [Fact]
    public async Task AnUnavailableModelLeavesTheObservationWithNoOpinionAndNoFailure()
    {
        // The default harness model is never ready, which is what the pipeline sees when the
        // feature is configured off.
        var harness = new PipelineTestHarness();

        var result = await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "An explosion damaged a tanker in the Gulf of Aden. Two crew were injured.",
            },
            CancellationToken.None);

        var stored = Assert.Single(harness.Observations.Committed);

        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.Null(stored.ModelSeverity);
        Assert.Null(stored.ModelVersion);

        // Null, not Unknown. "No second opinion was taken" and "the model assessed this as Unknown"
        // are different facts and must not collapse into one value.
        Assert.False(stored.ModelDisagrees);
    }

    [Fact]
    public async Task AThrowingModelDoesNotFailTheObservation()
    {
        var harness = new PipelineTestHarness { SeverityModel = new ThrowingSeverityModel() };

        var result = await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "Shelling damaged buildings in a border village overnight.",
            },
            CancellationToken.None);

        var stored = Assert.Single(harness.Observations.Committed);

        // The whole point: the observation is processed, persisted, and correlated exactly as it
        // would have been, minus the opinion.
        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.Null(stored.FailureReason);
        Assert.Null(stored.ModelSeverity);
    }

    [Fact]
    public async Task TheOpinionReachesTheApiProjection()
    {
        var harness = new PipelineTestHarness
        {
            SeverityModel = new ScriptedSeverityModel(Severity.High, 0.71, "test-model/v9"),
        };

        await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "A vessel was hijacked with crew aboard off the Gulf of Guinea.",
                DeclaredSeverity = Severity.Medium,
            },
            CancellationToken.None);

        var response = ObservationResponse.FromDomain(harness.Observations.Committed.Single());

        Assert.NotNull(response.ModelSeverity);
        Assert.Equal(Severity.High, response.ModelSeverity.Severity);
        Assert.Equal(0.71, response.ModelSeverity.Confidence);
        Assert.Equal("test-model/v9", response.ModelSeverity.ModelVersion);
        Assert.True(response.ModelSeverity.DisagreesWithApplied);
    }

    /// <summary>
    /// The same guarantee, at the other end of the interface.
    /// <para>
    /// <c>IsReady</c> is not a field read. In the shipped model it is what forces the lazy training
    /// to run, so it is the call most likely to throw — and because <see cref="Lazy{T}"/> caches a
    /// failure and rethrows it, once it does every later observation meets the same exception. A
    /// second opinion that cannot be obtained must cost the opinion and nothing else, whichever
    /// member of the interface fails to produce it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AModelThatCannotSayWhetherItIsReadyDoesNotFailTheObservation()
    {
        var harness = new PipelineTestHarness { SeverityModel = new UnreadableSeverityModel() };

        var result = await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "Shelling damaged buildings in a border village overnight.",
            },
            CancellationToken.None);

        var stored = Assert.Single(harness.Observations.Committed);

        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.Null(stored.FailureReason);
        Assert.Null(stored.ModelSeverity);
    }

    [Fact]
    public void TheDomainRefusesAPredictionItCannotAttribute()
    {
        var observation = new RawObservation(
            Guid.CreateVersion7(),
            ObservationKind.News,
            "fixture",
            "Some report text.",
            sourceIdentifier: null,
            receivedAt: DateTimeOffset.UtcNow,
            provenance: ObservationProvenance.Polled);

        // A stored prediction with no model behind it could never be reproduced or compared, so it
        // is rejected rather than recorded with a blank.
        Assert.Throws<DomainException>(() => observation.RecordModelSeverity(Severity.High, 0.5, "  "));
        Assert.Throws<DomainException>(() => observation.RecordModelSeverity(Severity.High, 1.4, "model/v1"));
    }
}

/// <summary>A model that is ready and then throws, which is the failure the pipeline must absorb.</summary>
internal sealed class ThrowingSeverityModel : ISeverityModel
{
    public string Version => "throwing/v1";

    public bool IsReady => true;

    public Task<SeverityPrediction?> PredictAsync(SeverityFeatures features, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The model failed.");
}

/// <summary>
/// Throws from <c>IsReady</c> rather than from the prediction. This is what a model whose training
/// failed on a platform missing its native dependencies actually looks like to the pipeline.
/// </summary>
internal sealed class UnreadableSeverityModel : ISeverityModel
{
    public string Version => "unreadable/v1";

    public bool IsReady => throw new InvalidOperationException("The model could not be trained.");

    public Task<SeverityPrediction?> PredictAsync(SeverityFeatures features, CancellationToken cancellationToken) =>
        Task.FromResult<SeverityPrediction?>(null);
}
