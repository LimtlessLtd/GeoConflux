using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Closes a defect this repository documented rather than hid: correlation read its candidate
/// incidents and wrote a new one without holding anything across the two, so two workers processing
/// simultaneous reports of one event each saw an empty candidate list and each opened an incident.
/// <para>
/// The test drives real processor scopes against a real SQLite database concurrently, which is the
/// only way to reproduce it: the in-memory fakes share one unit of work and cannot exhibit two
/// uncommitted writes racing.
/// </para>
/// <para>
/// It was checked against the defect rather than assumed to cover it. With the correlation gate
/// removed this test fails on every run; with it in place it passes on every run. That check matters
/// because the first version of this test passed either way — see <c>ProcessTogetherAsync</c> for
/// why, and for what had to change before it reproduced anything at all.
/// </para>
/// </summary>
public sealed class ConcurrentCorrelationTests
{
    private const int SimultaneousReports = 8;

    [Fact]
    public async Task SimultaneousReportsOfOneEventProduceOneIncident()
    {
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?>
            {
                // The background processor is off so this test owns the work, but the concurrency it
                // would have used is what is being reproduced here.
                ["Pipeline:ProcessorConcurrency"] = "4",

                // No demo seed records, so "how many incidents exist" is a question about what this
                // test produced rather than about what the host shipped with.
                ["Seed:Enabled"] = "false",
            });

        // Realises the host and runs database initialisation before any scope is resolved.
        using var client = factory.CreateClient();

        var envelopes = Enumerable
            .Range(0, SimultaneousReports)
            .Select(BuildReport)
            .ToArray();

        var results = await ProcessTogetherAsync(factory, envelopes);

        // Every report survived. A correlation fix that achieved consistency by dropping evidence
        // would be a worse bug than the one it replaced.
        Assert.All(results, result => Assert.Equal(ProcessingOutcome.Persisted, result.Outcome));

        var incidentIds = results.Select(result => result.IncidentId).Distinct().ToArray();
        Assert.Single(incidentIds);

        // Exactly one report opened the incident; the other seven joined it.
        Assert.Equal(1, results.Count(result => result.IncidentCreated));

        await using var verification = factory.Services.CreateAsyncScope();
        var incidents = await verification.ServiceProvider
            .GetRequiredService<IIncidentQueryService>()
            .ListAsync(new IncidentSearch(100), CancellationToken.None);

        var incident = Assert.Single(incidents);
        Assert.Equal(SimultaneousReports, incident.ObservationCount);
    }

    [Fact]
    public async Task SimultaneousReportsOfDifferentEventsStayApart()
    {
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?> { ["Seed:Enabled"] = "false" });

        using var client = factory.CreateClient();

        // Same instant, same category, far apart. Serialising correlation must not start merging
        // things that are merely processed at the same time.
        var envelopes = new[]
        {
            BuildReport(0) with { DeclaredLatitude = 12.585, DeclaredLongitude = 43.334 },
            BuildReport(1) with { DeclaredLatitude = 3.000, DeclaredLongitude = 3.000 },
            BuildReport(2) with { DeclaredLatitude = 43.000, DeclaredLongitude = 34.000 },
        };

        var results = await ProcessTogetherAsync(factory, envelopes);

        Assert.Equal(3, results.Select(result => result.IncidentId).Distinct().Count());
    }

    /// <summary>
    /// Runs every envelope through its own processor scope, genuinely in parallel.
    /// <para>
    /// <c>Task.Run</c> and the release barrier are both load-bearing. EF Core's SQLite provider
    /// completes its reads synchronously, because there is no real I/O to await, so starting these
    /// as plain async lambdas runs each one to completion during enumeration and the "concurrent"
    /// test executes strictly sequentially — passing whether or not the race is fixed. Dispatching
    /// to the thread pool and holding every worker at a barrier until all of them are ready is what
    /// actually puts two candidate reads in flight before either write commits.
    /// </para>
    /// </summary>
    private static async Task<ObservationProcessingResult[]> ProcessTogetherAsync(
        PipelineFactory factory,
        ObservationEnvelope[] envelopes)
    {
        using var ready = new CountdownEvent(envelopes.Length);
        using var release = new ManualResetEventSlim(false);

        var workers = envelopes.Select(envelope => Task.Run(() =>
        {
            // Resolved before the barrier so scope construction is not what the workers stagger on.
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

    /// <param name="index">
    /// Varies the source identifier only. The payload describes one event, so every report has a
    /// distinct fingerprint and none is rejected as an exact redelivery: the pipeline has to reach
    /// correlation to decide these belong together, which is the stage under test.
    /// </param>
    private static ObservationEnvelope BuildReport(int index) => new()
    {
        SourceName = $"test:outlet-{index}",
        Kind = ObservationKind.News,
        SourceIdentifier = $"concurrent-correlation-{index}",
        Title = "Vessel reports small craft approach",
        Content = "A cargo vessel transiting the strait reported an approach by small craft "
            + $"before escorts responded. Filed by outlet {index}.",
        OccurredAt = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),

        // Declared coordinates so every report resolves to the same point and the correlator is
        // deciding on measured co-location rather than on the gazetteer.
        DeclaredLatitude = 12.585,
        DeclaredLongitude = 43.334,
        DeclaredEventType = EventType.MaritimeIncident,
        DeclaredSeverity = Severity.Medium,
        Provenance = ObservationProvenance.Polled,
    };
}
