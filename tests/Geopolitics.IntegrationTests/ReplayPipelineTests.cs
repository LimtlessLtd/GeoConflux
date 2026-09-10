using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Drives the recorded replay stream through the real host — queue, background processor, SQLite,
/// and realtime publication — and asserts on what the API then serves. This is the Sprint 2
/// definition of done: an ingested event reaches the dashboard without a page refresh.
/// </summary>
public sealed class ReplayPipelineTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task TheRecordedStreamIsIngestedCorrelatedAndServedByTheApi()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        var observations = await WaitForObservationsAsync(client, expected: 8);

        // Every recorded record is accounted for, including the deliberate redelivery.
        Assert.Equal(8, observations.Count);

        var duplicate = Assert.Single(observations, value => value.Status == ObservationStatus.Duplicate);
        Assert.NotNull(duplicate.DuplicateOfObservationId);

        // Two outlets reported one event, so that event must be a single incident with two sources.
        var incidents = await client.GetFromJsonAsync<List<IncidentResponse>>("/api/incidents?take=200", Json);
        Assert.NotNull(incidents);

        var correlated = incidents.Where(incident => incident.ObservationCount > 1).ToArray();
        Assert.NotEmpty(correlated);

        // The unmappable place name is stored without coordinates rather than being dropped or invented.
        var unresolved = observations.Single(value => value.LocationName == "Somewhere Unmapped");
        Assert.Null(unresolved.Location);
        Assert.NotNull(unresolved.LocationResolutionNote);
        Assert.Equal(ObservationStatus.Persisted, unresolved.Status);

        // Provider-supplied coordinates were used verbatim.
        var satellite = observations.Single(value => value.Kind == ObservationKind.Satellite);
        Assert.NotNull(satellite.Location);
        Assert.Equal(12.61, satellite.Location.Latitude, precision: 2);

        // Nothing from replay may present itself as live reporting.
        Assert.All(observations, observation => Assert.True(observation.IsDemo));
    }

    [Fact]
    public async Task RealtimePublicationHappensOnlyAfterStateIsCommitted()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        await WaitForObservationsAsync(client, expected: 8);

        Assert.NotEmpty(factory.Notifier.Created);

        // Every announced incident must already be retrievable from the API, which is only true if
        // publication follows the save rather than racing it.
        foreach (var announced in factory.Notifier.Created)
        {
            var response = await client.GetAsync(new Uri($"/api/incidents/{announced.Id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task AManuallySubmittedObservationIsQueuedAndProcessed()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new
            {
                sourceName = "analyst-desk",
                title = "Manually submitted maritime report",
                content = "An analyst recorded a vessel being detained near the Strait of Hormuz.",
                locationName = "Strait of Hormuz",
                eventType = "MaritimeIncident",
                severity = "Medium",
            },
            Json);

        // Accepted, not Created: the observation is queued and its outcome is not yet known.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observations = await WaitForObservationsAsync(client, expected: 1);
        var submitted = Assert.Single(observations);

        Assert.Equal(ObservationKind.Manual, submitted.Kind);
        Assert.Equal("manual:analyst-desk", submitted.SourceName);
        Assert.False(submitted.IsDemo);
        Assert.NotNull(submitted.Location);
    }

    [Fact]
    public async Task ManualSubmissionStillWorksAfterTheRecordedSourcesHaveFinished()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        // Let the finite replay stream run to exhaustion first.
        await WaitForObservationsAsync(client, expected: 8);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new
            {
                sourceName = "analyst-desk",
                title = "Submitted after replay finished",
                content = "An analyst recorded a vessel detained near the Strait of Hormuz.",
                locationName = "Strait of Hormuz",
            },
            Json);

        // Regression: the pump used to close the queue when its sources ran out, which permanently
        // broke this endpoint even though the host was still running normally.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observations = await WaitForObservationsAsync(client, expected: 9);
        Assert.Contains(observations, value => value.SourceName == "manual:analyst-desk");
    }

    [Fact]
    public async Task AnInvalidSubmissionIsRejectedWithoutReachingTheQueue()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new { sourceName = "analyst-desk", content = "" },
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheQueueHealthCheckIsReported()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync(new Uri("/api/health", UriKind.Relative));

        Assert.Contains("processing-queue", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostShutsDownCleanlyWhileThePipelineIsRunning()
    {
        var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        // Start work, then tear the host down mid-flight. Disposal must complete rather than hang on
        // a background worker that ignores its cancellation token.
        await client.GetStringAsync(new Uri("/api/health", UriKind.Relative));

        var disposal = Task.Run(factory.Dispose);
        var finished = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(disposal, finished);
        await disposal;
    }

    /// <summary>
    /// Polls the API until the expected number of observations is visible. The pipeline is
    /// asynchronous by design, so the test waits on the observable outcome rather than reaching into
    /// the queue or sleeping for a guessed duration.
    /// </summary>
    private static async Task<List<ObservationResponse>> WaitForObservationsAsync(HttpClient client, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<ObservationResponse> observations = [];

        while (DateTime.UtcNow < deadline)
        {
            observations = await client.GetFromJsonAsync<List<ObservationResponse>>("/api/observations?take=100", Json)
                ?? [];

            if (observations.Count >= expected)
            {
                return observations;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Expected at least {expected} observations within the timeout but saw {observations.Count}.");
        return observations;
    }
}
