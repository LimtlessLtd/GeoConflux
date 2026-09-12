using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Infrastructure.Persistence;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// What the pipeline does when the commit itself fails.
/// <para>
/// These run against real SQLite because both behaviours under test are properties of the database
/// and the EF change tracker rather than of the processor's own logic: the filtered unique index is
/// what rejects a concurrent redelivery, and it is the tracker that decides what a second save
/// attempt actually writes. In-memory fakes share one unit of work and can exhibit neither.
/// </para>
/// </summary>
public sealed class PipelineFailureBoundaryTests
{
    /// <summary>
    /// Two workers holding the same payload, racing. Both pass the read-then-insert duplicate check
    /// because neither can see the other's uncommitted write, so the filtered unique index is what
    /// settles it — and the loser should be reported as the duplicate it is.
    /// </summary>
    [Fact]
    public async Task AConcurrentRedeliveryIsReportedAsADuplicateRatherThanAFailure()
    {
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?> { ["Seed:Enabled"] = "false" });

        using var client = factory.CreateClient();

        // Identical in every field the fingerprint covers, so both envelopes hash to one value.
        var envelopes = Enumerable.Range(0, 2).Select(_ => Identical()).ToArray();

        var results = await ProcessTogetherAsync(factory, envelopes);

        Assert.Equal(1, results.Count(result => result.Outcome == ProcessingOutcome.Persisted));

        // The loser is a duplicate, not a failure. The distinction is the whole point: a duplicate is
        // an expected outcome the pipeline handles, a failure is an error someone is meant to read.
        Assert.Equal(1, results.Count(result => result.Outcome == ProcessingOutcome.Duplicate));
        Assert.DoesNotContain(results, result => result.Outcome == ProcessingOutcome.Failed);

        await using var scope = factory.Services.CreateAsyncScope();
        var observations = await scope.ServiceProvider
            .GetRequiredService<IObservationQueryService>()
            .ListRecentAsync(50, CancellationToken.None);

        // Both deliveries are retained. Losing the race must not lose the evidence that it happened.
        Assert.Equal(2, observations.Count);
        Assert.Single(observations, value => value.Status == ObservationStatus.Persisted);
        Assert.Single(observations, value => value.Status == ObservationStatus.Duplicate);
    }

    private static async Task<ObservationProcessingResult[]> ProcessTogetherAsync(
        PipelineFactory factory,
        ObservationEnvelope[] envelopes)
    {
        using var ready = new CountdownEvent(envelopes.Length);
        using var release = new ManualResetEventSlim(false);

        var workers = envelopes.Select(envelope => Task.Run(() =>
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();

            ready.Signal();
            release.Wait();

            return processor.ProcessAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
        })).ToArray();

        ready.Wait(TimeSpan.FromSeconds(30));
        release.Set();

        return await Task.WhenAll(workers);
    }

    /// <summary>
    /// One report, delivered twice. Every field the fingerprint is computed over is fixed, which is
    /// what makes these two envelopes the same observation rather than two similar ones.
    /// </summary>
    private static ObservationEnvelope Identical() => new()
    {
        SourceName = "test:wire",
        Kind = ObservationKind.News,
        SourceIdentifier = "concurrent-redelivery-1",
        Title = "Convoy halted at the crossing",
        Content = "A humanitarian convoy was halted at the crossing for several hours before proceeding.",
        OccurredAt = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
        DeclaredLatitude = 31.522,
        DeclaredLongitude = 34.453,
        DeclaredEventType = EventType.Protest,
        DeclaredSeverity = Severity.Medium,
        Provenance = ObservationProvenance.Polled,
    };

    /// <summary>
    /// A commit that fails must leave nothing behind.
    /// <para>
    /// The processor retains the source payload after a failure so the report can be diagnosed and
    /// reprocessed, which is right. What it must not do is commit the rest of the unit of work on the
    /// way: the incident that failed attempt had staged is not evidence of anything, and publishing
    /// it while reporting the observation as failed puts a record on the dashboard that the pipeline
    /// itself does not believe it created.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFailedCommitDoesNotLeaveTheIncidentBehind()
    {
        var failing = new FailOnceOnSave();

        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?> { ["Seed:Enabled"] = "false" },
            configureServices: services =>
            {
                services.RemoveAll<IIncidentRepository>();
                services.AddScoped<IIncidentRepository>(provider => new FailingIncidentRepository(
                    new EfIncidentRepository(provider.GetRequiredService<GeopoliticsDbContext>()),
                    failing));
            });

        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();
            var result = await processor.ProcessAsync(Identical(), CancellationToken.None);

            Assert.Equal(ProcessingOutcome.Failed, result.Outcome);
        }

        await using var verification = factory.Services.CreateAsyncScope();

        var observations = await verification.ServiceProvider
            .GetRequiredService<IObservationQueryService>()
            .ListRecentAsync(50, CancellationToken.None);

        // The payload is kept. That part of the retention path is doing its job.
        var retained = Assert.Single(observations);
        Assert.Equal(ObservationStatus.Failed, retained.Status);

        var incidents = await verification.ServiceProvider
            .GetRequiredService<IIncidentQueryService>()
            .ListAsync(new IncidentSearch(100), CancellationToken.None);

        // The incident is not. One committed here would be sourced entirely from an observation the
        // pipeline reported as failed and never announced.
        Assert.Empty(incidents);

        // The audit trail is, though, and the difference is deliberate. Inference rows carry no
        // foreign key precisely so the record of having called a model outlives the observation it
        // describes; a recovery that dropped them would undo that on the one path it matters on.
        var inferences = await verification.ServiceProvider
            .GetRequiredService<IAiInferenceRepository>()
            .ListByObservationAsync(retained.Id, CancellationToken.None);

        Assert.NotEmpty(inferences);
    }

    /// <summary>Trips the first save and then gets out of the way.</summary>
    private sealed class FailOnceOnSave
    {
        private int tripped;

        public bool ShouldFail() => Interlocked.Exchange(ref tripped, 1) == 0;
    }

    /// <summary>
    /// The real repository with one failure injected. A decorator rather than a stub so everything
    /// around the failure — the change tracker, the transaction, the retention path — is the
    /// production code being tested.
    /// </summary>
    private sealed class FailingIncidentRepository(EfIncidentRepository inner, FailOnceOnSave trigger) : IIncidentRepository
    {
        public Task<GeopoliticalIncident?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<GeopoliticalIncident>> ListAsync(IncidentSearch search, CancellationToken cancellationToken) =>
            inner.ListAsync(search, cancellationToken);

        public Task<IReadOnlyList<GeopoliticalIncident>> ListCorrelationCandidatesAsync(
            EventType eventType,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken) =>
            inner.ListCorrelationCandidatesAsync(eventType, windowStart, windowEnd, cancellationToken);

        public Task<IReadOnlyList<GeopoliticalIncident>> ListWithinAsync(
            GeoBoundingBox boundingBox,
            DateTimeOffset? occurredAfter,
            int take,
            CancellationToken cancellationToken) =>
            inner.ListWithinAsync(boundingBox, occurredAfter, take, cancellationToken);

        public Task AddAsync(GeopoliticalIncident incident, CancellationToken cancellationToken) =>
            inner.AddAsync(incident, cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => trigger.ShouldFail()
            ? throw new InvalidOperationException("Injected commit failure.")
            : inner.SaveChangesAsync(cancellationToken);
    }
}
