using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

public sealed class ObservationProcessorTests
{
    [Fact]
    public async Task FirstObservationOpensAnIncidentAndIsPublished()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope("Vessel boarded near the strait.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.True(result.IncidentCreated);
        Assert.Single(harness.Incidents.Committed);
        Assert.Single(harness.Notifier.Created);
        Assert.Equal(ObservationStatus.Persisted, harness.Observations.Committed[0].Status);
    }

    [Fact]
    public async Task IdenticalRedeliveryIsRecordedAsADuplicateRatherThanDiscarded()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334);
        var processor = harness.BuildProcessor();
        var envelope = PipelineTestHarness.Envelope("Vessel boarded near the strait.", sourceIdentifier: "a-1");

        await processor.ProcessAsync(envelope, CancellationToken.None);
        var second = await processor.ProcessAsync(envelope, CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Duplicate, second.Outcome);

        // Only one incident, but both deliveries are retained so the duplicate stays auditable.
        Assert.Single(harness.Incidents.Committed);
        Assert.Equal(2, harness.Observations.Committed.Count);

        var duplicate = harness.Observations.Committed.Single(value => value.Status == ObservationStatus.Duplicate);
        Assert.NotNull(duplicate.DuplicateOfObservationId);
        Assert.Equal(1, harness.Incidents.Committed[0].ObservationCount);
    }

    [Fact]
    public async Task TwoSourcesReportingTheSameEventCorrelateIntoOneIncident()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-30);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Skiffs approached a cargo vessel.",
                sourceName: "wire-a",
                sourceIdentifier: "a-1",
                eventType: EventType.Piracy,
                occurredAt: occurredAt),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "A second outlet reports skiffs approaching a cargo ship.",
                sourceName: "wire-b",
                sourceIdentifier: "b-1",
                eventType: EventType.Piracy,
                occurredAt: occurredAt.AddMinutes(7)),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Persisted, second.Outcome);
        Assert.False(second.IncidentCreated);
        Assert.Single(harness.Incidents.Committed);
        Assert.Equal(2, harness.Incidents.Committed[0].ObservationCount);
        Assert.Single(harness.Notifier.Updated);
    }

    [Fact]
    public async Task AnIncidentTakesTheSeverityOfItsWorstCorroboratedReport()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334);
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Initial report.",
                sourceIdentifier: "a-1",
                eventType: EventType.Conflict,
                severity: Severity.Low,
                occurredAt: occurredAt),
            CancellationToken.None);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Follow-up report with casualties.",
                sourceIdentifier: "b-1",
                eventType: EventType.Conflict,
                severity: Severity.Critical,
                occurredAt: occurredAt),
            CancellationToken.None);

        Assert.Equal(Severity.Critical, harness.Incidents.Committed[0].Severity);
    }

    [Fact]
    public async Task AnObservationWithNoResolvableLocationIsStillStored()
    {
        var harness = new PipelineTestHarness();
        harness.LocationResolver.Behaviour = _ => LocationResolution.Failed("Not in the gazetteer.");
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope("Clashes near an unnamed settlement.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);

        var stored = Assert.Single(harness.Observations.Committed);
        Assert.Null(stored.Location);

        // Being unplaced is a normal outcome, so it is explained without being marked a failure.
        Assert.Equal("Not in the gazetteer.", stored.LocationResolutionNote);
        Assert.Null(stored.FailureReason);
        Assert.Equal(ObservationStatus.Persisted, stored.Status);

        // The incident exists but is honestly unplaced rather than given invented coordinates.
        Assert.Null(harness.Incidents.Committed[0].Location);
    }

    [Fact]
    public async Task AResolverOutageDoesNotPreventPersistence()
    {
        var harness = new PipelineTestHarness();
        harness.LocationResolver.Behaviour = _ => throw new InvalidOperationException("Resolver offline.");
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope("A report during a resolver outage.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.Single(harness.Observations.Committed);
    }

    [Fact]
    public async Task ARealtimePublishFailureDoesNotUndoCommittedWork()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334);
        harness.Notifier.ThrowOnPublish = new InvalidOperationException("Hub unavailable.");
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope("A report published while the hub is down.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        // The observation and incident are committed; only the announcement was lost.
        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.Single(harness.Observations.Committed);
        Assert.Single(harness.Incidents.Committed);
        Assert.Empty(harness.Notifier.Created);
    }

    [Fact]
    public async Task AFailureMidPipelineRetainsTheSourcePayload()
    {
        var harness = new PipelineTestHarness
        {
            Correlator = new StubCorrelator
            {
                Behaviour = _ => throw new InvalidOperationException("Correlation exploded."),
            },
        };
        harness.ResolveAllTo(12.585, 43.334);
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope("A report that breaks correlation.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Incidents.Committed);

        // Evidence survives the failure so the report can be diagnosed and reprocessed.
        var retained = Assert.Single(harness.Observations.Committed);
        Assert.Equal(ObservationStatus.Failed, retained.Status);
        Assert.Contains("Correlation exploded.", retained.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBeingRecordedAsAFailure()
    {
        var harness = new PipelineTestHarness
        {
            Correlator = new StubCorrelator { Behaviour = _ => throw new OperationCanceledException() },
        };
        harness.ResolveAllTo(12.585, 43.334);
        var processor = harness.BuildProcessor();

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(
            PipelineTestHarness.Envelope("A report interrupted by shutdown.", sourceIdentifier: "a-1"),
            CancellationToken.None));

        // Shutdown must not leave a misleading failure row behind; the source can redeliver.
        Assert.Empty(harness.Observations.Committed);
    }

    [Fact]
    public async Task ConcurrentUniquenessViolationIsTreatedAsADuplicate()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334);
        harness.Observations.SaveException = new DuplicateObservationException("abc123");
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope("A report that lost a uniqueness race.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Duplicate, result.Outcome);
    }
}
