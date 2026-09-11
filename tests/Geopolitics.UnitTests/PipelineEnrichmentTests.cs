using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// How the pipeline behaves around enrichment. These run the shipped
/// <c>ObservationProcessor</c> rather than a re-implementation, so they assert the real stage
/// ordering: evidence is retained whatever the provider does, every attempt leaves an audit record,
/// and a deterministic classification is only replaced by something demonstrably better.
/// </summary>
public sealed class PipelineEnrichmentTests
{
    [Fact]
    public async Task AnEnrichedObservationAdoptsTheModelsSummaryCategoryAndConfidence()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            summary: "Two skiffs approached a cargo vessel.",
            eventType: EventType.Piracy,
            severity: Severity.High,
            confidence: 0.88,
            language: "ar",
            entities: [new ExtractedEntity("Combined Maritime Forces", EntityType.Organisation)]);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("نص عربي عن حادث بحري", sourceIdentifier: "a-1"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Equal("Two skiffs approached a cargo vessel.", observation.Summary);
        Assert.Equal(EventType.Piracy, observation.EventType);
        Assert.Equal(Severity.High, observation.Severity);
        Assert.Equal(0.88, observation.ClassificationConfidence);
        Assert.StartsWith("ai:", observation.ClassificationMethod, StringComparison.Ordinal);
        Assert.Equal("ar", observation.DetectedLanguage);
        Assert.Equal("Combined Maritime Forces", Assert.Single(observation.Entities).Name);
    }

    [Fact]
    public async Task ASuccessfulEnrichmentIsRecordedAsAnAuditableInference()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(0, 0);
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(confidence: 0.7);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A report.", sourceIdentifier: "b-1"),
            CancellationToken.None);

        var inference = Assert.Single(harness.Inferences.Committed);
        Assert.Equal(AiInferenceOutcome.Succeeded, inference.Outcome);
        Assert.Equal(0.7, inference.Confidence);
        Assert.Equal(harness.Observations.Committed[0].Id, inference.ObservationId);
        Assert.False(string.IsNullOrWhiteSpace(inference.PromptVersion));
        Assert.NotNull(inference.StructuredOutput);
    }

    [Theory]
    [InlineData(AiInferenceOutcome.ProviderFailed)]
    [InlineData(AiInferenceOutcome.ValidationFailed)]
    public async Task AFailedEnrichmentKeepsTheObservationAndTheDeterministicClassification(AiInferenceOutcome outcome)
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(0, 0);
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Failure(outcome, "the provider was unreachable");

        var result = await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope(
                "Pirates hijacked a tanker and the crew was detained.",
                sourceIdentifier: "c-1"),
            CancellationToken.None);

        // The observation is still processed end to end; only classification quality degrades.
        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Equal(EventType.Piracy, observation.EventType);
        Assert.Equal("keyword", observation.ClassificationMethod);
        Assert.True(observation.ClassificationConfidence > 0);

        // The failure is visible rather than absorbed silently.
        var inference = Assert.Single(harness.Inferences.Committed);
        Assert.Equal(outcome, inference.Outcome);
        Assert.Null(inference.Confidence);
        Assert.Contains("unreachable", inference.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfidentModelDoesNotOverwriteTheTransparentHeuristic()
    {
        var harness = new PipelineTestHarness();
        harness.Enrichment.MinimumAcceptedConfidence = 0.5;
        harness.ResolveAllTo(0, 0);
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            summary: "A summary the model was not sure about.",
            eventType: EventType.Sanctions,
            confidence: 0.2,
            language: "ru",
            locationName: "Kerch Strait",
            entities: [new ExtractedEntity("Task Group Kestrel", EntityType.Organisation)]);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Protest and demonstration reported downtown.", sourceIdentifier: "d-1"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Equal(EventType.Protest, observation.EventType);
        Assert.Equal("keyword", observation.ClassificationMethod);
        Assert.NotEqual("A summary the model was not sure about.", observation.Summary);

        // Rejected for use, but still recorded: the attempt happened and stays reviewable.
        Assert.Equal(AiInferenceOutcome.Succeeded, Assert.Single(harness.Inferences.Committed).Outcome);
    }

    [Fact]
    public async Task AnUnconfidentModelsFactualExtractionsAreStillKept()
    {
        // Confidence describes certainty in the classification. A model unsure whether a report is
        // piracy or a maritime incident can still be right that the text is Russian and names the
        // Kerch Strait, and those are what let an otherwise unreadable report be placed on the map.
        var harness = new PipelineTestHarness();
        harness.Enrichment.MinimumAcceptedConfidence = 0.5;
        harness.LocationResolver.Behaviour = request => request.LocationName == "Kerch Strait"
            ? new LocationResolution(
                new GeoLocation("Kerch Strait", null, 45.3, 36.5),
                LocationResolutionMethod.Gazetteer,
                0.7,
                null)
            : LocationResolution.Failed("Not in the local gazetteer.");

        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            confidence: 0.2,
            language: "ru",
            locationName: "Kerch Strait",
            entities: [new ExtractedEntity("Task Group Kestrel", EntityType.Organisation)]);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Report with no declared place.", sourceIdentifier: "d-2"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        Assert.Equal("ru", observation.DetectedLanguage);
        Assert.Equal("Kerch Strait", observation.LocationName);
        Assert.Equal("Task Group Kestrel", Assert.Single(observation.Entities).Name);
        Assert.NotNull(observation.Location);

        // The judgement the model was unsure about is still not adopted.
        Assert.Equal("keyword", observation.ClassificationMethod);
    }

    [Fact]
    public async Task ASourceDeclaredCategoryOutranksAnInferredOne()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(0, 0);
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            eventType: EventType.Protest,
            severity: Severity.Low,
            confidence: 0.95);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope(
                "Structured record from an event database.",
                sourceIdentifier: "e-1",
                eventType: EventType.NavalIncident,
                severity: Severity.Critical),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Equal(EventType.NavalIncident, observation.EventType);
        Assert.Equal(Severity.Critical, observation.Severity);

        // The provenance says both contributed, so a reader is not misled about which won.
        Assert.StartsWith("source-declared+ai:", observation.ClassificationMethod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateIsNotSentForEnrichment()
    {
        // Enrichment is the expensive stage. Paying for it on a re-delivery that is about to be
        // discarded is pure waste, so deduplication runs first.
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(0, 0);
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success();

        var envelope = PipelineTestHarness.Envelope("A repeated report.", sourceIdentifier: "f-1");
        var processor = harness.BuildProcessor();

        await processor.ProcessAsync(envelope, CancellationToken.None);
        var second = await processor.ProcessAsync(envelope, CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Duplicate, second.Outcome);
        Assert.Single(harness.EnrichmentService.Requests);
        Assert.Single(harness.Inferences.Committed);
    }

    [Fact]
    public async Task ALocationNameFromTheModelIsResolvedByTheDeterministicResolverNotAdopted()
    {
        // ADR 005: the model may name a place; only the resolver may position it.
        var harness = new PipelineTestHarness();
        LocationResolutionRequest? seen = null;
        harness.LocationResolver.Behaviour = request =>
        {
            seen = request;
            return new LocationResolution(
                new GeoLocation("Gulf of Aden", null, 12.5, 47.5),
                LocationResolutionMethod.Gazetteer,
                0.7,
                null);
        };
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(locationName: "Gulf of Aden");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A report with no declared place.", sourceIdentifier: "g-1"),
            CancellationToken.None);

        Assert.Equal("Gulf of Aden", seen!.LocationName);
        Assert.Null(seen.DeclaredLatitude);
        Assert.Null(seen.DeclaredLongitude);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Equal(12.5, observation.Location!.Latitude);
    }

    [Fact]
    public async Task AnUnresolvableModelPlaceNameLeavesTheObservationUnplacedRatherThanGuessed()
    {
        var harness = new PipelineTestHarness();
        harness.LocationResolver.Behaviour = _ => LocationResolution.Failed("Not in the local gazetteer.");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(locationName: "A Village Nobody Indexed");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A report about somewhere obscure.", sourceIdentifier: "h-1"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Null(observation.Location);
        Assert.Equal("A Village Nobody Indexed", observation.LocationName);
        Assert.NotNull(observation.LocationResolutionNote);
    }

    [Fact]
    public async Task LosingTheAuditRecordDoesNotCostTheObservation()
    {
        // The inference is telemetry about processing. Losing telemetry is a smaller loss than
        // losing evidence, so a failure to write it must not fail the pipeline.
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(0, 0);
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success();
        harness.Inferences.AddException = new InvalidOperationException("audit store unavailable");

        var result = await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A report.", sourceIdentifier: "i-1"),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.Single(harness.Observations.Committed);
        Assert.Empty(harness.Inferences.Committed);
    }

    [Fact]
    public async Task TheIncidentReportsTheConfidenceOfItsBestSupportedEvidence()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        var processor = harness.BuildProcessor();

        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            eventType: EventType.Piracy,
            confidence: 0.55);
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("First outlet reports an approach.", sourceIdentifier: "j-1"),
            CancellationToken.None);

        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            eventType: EventType.Piracy,
            confidence: 0.91);
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("Second outlet reports the same approach.", sourceIdentifier: "j-2"),
            CancellationToken.None);

        var incident = Assert.Single(harness.Incidents.Committed);
        Assert.Equal(2, incident.ObservationCount);
        Assert.Equal(0.91, incident.ClassificationConfidence);

        // A later, vaguer report must not talk the incident's confidence back down.
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            eventType: EventType.Piracy,
            confidence: 0.4);
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("A third, vaguer mention.", sourceIdentifier: "j-3"),
            CancellationToken.None);

        Assert.Equal(0.91, Assert.Single(harness.Incidents.Committed).ClassificationConfidence);
    }
}
