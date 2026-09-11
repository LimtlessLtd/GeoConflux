using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// Minimal <see cref="IMeterFactory"/> so the processor can be built with its real diagnostics.
/// The tests assert on behaviour rather than on counter values, so this only needs to be functional.
/// </summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in meters)
        {
            meter.Dispose();
        }

        meters.Clear();
    }
}

/// <summary>Location resolver stub whose behaviour each test dictates outright.</summary>
public sealed class StubLocationResolver : ILocationResolver
{
    public Func<LocationResolutionRequest, LocationResolution> Behaviour { get; set; } =
        _ => LocationResolution.Failed("No resolver configured.");

    public Task<LocationResolution> ResolveAsync(LocationResolutionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Behaviour(request));
}

/// <summary>Records what the pipeline published, and can be made to fail on demand.</summary>
public sealed class RecordingNotifier : IIncidentNotifier
{
    public List<IncidentResponse> Created { get; } = [];

    public List<IncidentResponse> Updated { get; } = [];

    public List<ObservationResponse> Observations { get; } = [];

    public Exception? ThrowOnPublish { get; set; }

    public Task IncidentCreatedAsync(IncidentResponse incident, CancellationToken cancellationToken)
    {
        if (ThrowOnPublish is { } exception) throw exception;
        Created.Add(incident);
        return Task.CompletedTask;
    }

    public Task IncidentUpdatedAsync(IncidentResponse incident, CancellationToken cancellationToken)
    {
        if (ThrowOnPublish is { } exception) throw exception;
        Updated.Add(incident);
        return Task.CompletedTask;
    }

    public Task ObservationReceivedAsync(ObservationResponse observation, CancellationToken cancellationToken)
    {
        if (ThrowOnPublish is { } exception) throw exception;
        Observations.Add(observation);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Enrichment stub. Its default is a skipped result, so a test that does not care about AI sees the
/// deterministic pipeline exactly as it behaves when enrichment is switched off.
/// </summary>
public sealed class StubEnrichmentService : IEventEnrichmentService
{
    public Func<EnrichmentRequest, EnrichmentResult> Behaviour { get; set; } =
        _ => EnrichmentResult.Skipped("No enrichment configured.");

    public List<EnrichmentRequest> Requests { get; } = [];

    public Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Behaviour(request));
    }

    /// <summary>Builds a successful result, so tests state only the values they actually assert on.</summary>
    public static EnrichmentResult Success(
        string summary = "An enriched English summary of the report.",
        EventType eventType = EventType.MaritimeIncident,
        Severity severity = Severity.High,
        double confidence = 0.85,
        string? language = "en",
        string? locationName = null,
        IReadOnlyList<ExtractedEntity>? entities = null) => new(
            new ValidatedEnrichment(
                summary,
                eventType,
                severity,
                confidence,
                language,
                "Because the report describes an exchange of fire.",
                locationName,
                entities ?? []),
            AiInferenceOutcome.Succeeded,
            "Mock",
            "deterministic-stub",
            EnrichmentPrompt.Version,
            EnrichmentContract.SchemaVersion,
            Attempts: 1,
            LatencyMilliseconds: 12,
            StructuredOutput: "{\"schemaVersion\":1}",
            Error: null);

    public static EnrichmentResult Failure(AiInferenceOutcome outcome, string error) => new(
        null,
        outcome,
        "Mock",
        "deterministic-stub",
        EnrichmentPrompt.Version,
        EnrichmentContract.SchemaVersion,
        Attempts: 1,
        LatencyMilliseconds: 5,
        StructuredOutput: null,
        error);
}

/// <summary>Correlator stub for isolating processor behaviour from correlation scoring.</summary>
public sealed class StubCorrelator : IIncidentCorrelator
{
    public Func<RawObservation, CorrelationAssessment> Behaviour { get; set; } =
        _ => CorrelationAssessment.NewIncident("stub");

    public Task<CorrelationAssessment> CorrelateAsync(RawObservation observation, CancellationToken cancellationToken) =>
        Task.FromResult(Behaviour(observation));
}

/// <summary>
/// Assembles a real <see cref="ObservationProcessor"/> over fakes. Using the production processor
/// rather than a re-implementation is the point: these tests assert the shipped pipeline's ordering
/// and failure handling, not a parallel copy of it.
/// </summary>
public sealed class PipelineTestHarness
{
    public PipelineTestHarness(PipelineOptions? options = null)
    {
        Options = options ?? new PipelineOptions();
        Enrichment = new EnrichmentOptions();
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        Diagnostics = new PipelineDiagnostics(new TestMeterFactory());
        Observations = new FakeObservationRepository();
        Incidents = new FakeIncidentRepository { Observations = Observations };
        Notifier = new RecordingNotifier();
        Inferences = new FakeAiInferenceRepository();
        EnrichmentService = new StubEnrichmentService();
        Incidents.Inferences = Inferences;
        LocationResolver = new StubLocationResolver();
        Correlator = new DeterministicIncidentCorrelator(Incidents, Microsoft.Extensions.Options.Options.Create(Options));
        Normaliser = new ObservationNormaliser(new KeywordEventClassifier());
    }

    public PipelineOptions Options { get; }

    public EnrichmentOptions Enrichment { get; }

    public FakeTimeProvider Clock { get; }

    public PipelineDiagnostics Diagnostics { get; }

    public FakeObservationRepository Observations { get; }

    public FakeIncidentRepository Incidents { get; }

    public RecordingNotifier Notifier { get; }

    public FakeAiInferenceRepository Inferences { get; }

    public StubEnrichmentService EnrichmentService { get; }

    public StubLocationResolver LocationResolver { get; }

    public IIncidentCorrelator Correlator { get; set; }

    public IObservationNormaliser Normaliser { get; set; }

    public ObservationProcessor BuildProcessor() => new(
        Normaliser,
        Observations,
        Incidents,
        Inferences,
        EnrichmentService,
        Microsoft.Extensions.Options.Options.Create(Enrichment),
        LocationResolver,
        Correlator,
        Notifier,
        Diagnostics,
        Clock,
        NullLogger<ObservationProcessor>.Instance);

    /// <summary>Resolves everything to one fixed point, for tests about correlation rather than geocoding.</summary>
    public void ResolveAllTo(double latitude, double longitude, string name = "Test Location") =>
        LocationResolver.Behaviour = _ => new LocationResolution(
            new GeoLocation(name, null, latitude, longitude),
            LocationResolutionMethod.Gazetteer,
            0.7,
            null);

    public static ObservationEnvelope Envelope(
        string content,
        string sourceName = "test:source",
        string? sourceIdentifier = null,
        string? title = null,
        EventType? eventType = null,
        Severity? severity = null,
        DateTimeOffset? occurredAt = null,
        string? locationName = null) => new()
        {
            SourceName = sourceName,
            Kind = ObservationKind.News,
            Content = content,
            SourceIdentifier = sourceIdentifier,
            Title = title,
            DeclaredEventType = eventType,
            DeclaredSeverity = severity,
            OccurredAt = occurredAt,
            DeclaredLocationName = locationName,
        };
}
