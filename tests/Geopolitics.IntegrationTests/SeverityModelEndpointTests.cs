using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Drives the severity model through the real host.
/// <para>
/// The reason to test it here rather than only in the evaluation harness is that the host is where
/// the model is actually trained — lazily, from an embedded resource, inside a DI singleton. A
/// missing resource, a failed fit, or a registration that resolves a different lifetime all show up
/// here and nowhere else.
/// </para>
/// </summary>
public sealed class SeverityModelEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task TheModelReportsItselfReadyAndIdentifiesItsVersion()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var description = await client.GetFromJsonAsync<ModelRow>("/api/severity/model", Json);

        Assert.NotNull(description);
        Assert.True(description.Ready, "The model should train from the embedded corpus at startup.");

        // Trainer, feature-set version, and dataset version, so a stored prediction is traceable.
        Assert.Contains("sdca-maximum-entropy", description.Version, StringComparison.Ordinal);
        Assert.Contains("dataset-", description.Version, StringComparison.Ordinal);
        Assert.Contains("second opinion", description.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APredictionComesBackWithEveryClassScoreAndItsCaveat()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/severity/predict",
            new
            {
                title = "Explosion damages tanker hull",
                content = "An explosion tore a hole in the hull of a tanker. Two crew were injured and the vessel is taking on water.",
                eventType = "MaritimeIncident",
                sourceCount = 3,
                sourceConfidence = 0.8,
                entityCount = 1,
                hasLocation = true,
            },
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var prediction = await response.Content.ReadFromJsonAsync<PredictionRow>(Json);

        Assert.NotNull(prediction);
        Assert.Equal(4, prediction.Scores.Count);
        Assert.InRange(prediction.Confidence, 0, 1);
        Assert.Equal(1.0, prediction.Scores.Sum(value => value.Probability), 2);

        // The label leaves with the caveat attached. A confident-looking severity returned from soft
        // inputs is exactly the payload that needs one.
        Assert.Contains("synthetic", prediction.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never allowed to set", prediction.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEmptyReportIsRejected()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/severity/predict", new { title = "Nothing" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnOversizedReportIsRejectedBeforeItReachesTheModel()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        // Featurisation is linear in input length and runs under a lock, so an unbounded body here
        // would stall every other prediction. The request-size limit alone still admits megabytes.
        var response = await client.PostAsJsonAsync(
            "/api/severity/predict",
            new { content = new string('a', 20_001) },
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ADisabledModelAnswersUnavailableRatherThanGuessing()
    {
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?> { ["SeverityModel:Enabled"] = "false" });
        using var client = factory.CreateClient();

        var description = await client.GetFromJsonAsync<ModelRow>("/api/severity/model", Json);
        Assert.NotNull(description);
        Assert.False(description.Ready);

        var response = await client.PostAsJsonAsync(
            "/api/severity/predict",
            new { content = "A vessel was hijacked with crew aboard." },
            Json);

        // 503, not a default severity. A deployment that turned the model off must not be
        // indistinguishable from one whose model assessed the report and found it unremarkable.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ProcessedObservationsCarryTheModelsOpinionThroughTheApi()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        var observations = await WaitForObservationsAsync(client);

        // Every observation the pipeline actually processed carries an opinion; duplicates are
        // stopped before the model is reached, which is deliberate and is why they are excluded.
        var scored = observations
            .Where(value => value.Status is not ("Duplicate" or "Failed"))
            .ToArray();

        Assert.NotEmpty(scored);
        Assert.All(scored, observation =>
        {
            Assert.NotNull(observation.ModelSeverity);
            Assert.Contains("sdca-maximum-entropy", observation.ModelSeverity.ModelVersion, StringComparison.Ordinal);
            Assert.InRange(observation.ModelSeverity.Confidence, 0, 1);
        });

        // The applied severity is never the model's doing. Where the two differ, the response says
        // so, and the stored severity is still the one the pipeline acted on.
        Assert.All(scored, observation => Assert.Equal(
            observation.ModelSeverity!.DisagreesWithApplied,
            observation.ModelSeverity.Severity != observation.Severity));
    }

    [Fact]
    public async Task ADisabledModelLeavesProcessedObservationsWithoutAnOpinion()
    {
        using var factory = new PipelineFactory(
            runPipeline: true,
            runSources: true,
            settings: new Dictionary<string, string?> { ["SeverityModel:Enabled"] = "false" });
        using var client = factory.CreateClient();

        var observations = await WaitForObservationsAsync(client);

        // The pipeline still ran end to end; it simply took no second opinion.
        Assert.NotEmpty(observations);
        Assert.All(observations, observation => Assert.Null(observation.ModelSeverity));
    }

    private static async Task<List<ObservationRow>> WaitForObservationsAsync(HttpClient client)
    {
        const int RecordedObservations = 11;

        for (var attempt = 0; attempt < 300; attempt++)
        {
            var observations = await client.GetFromJsonAsync<List<ObservationRow>>("/api/observations?take=200", Json);

            if (observations is { Count: >= RecordedObservations })
            {
                return observations;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("The recorded stream did not finish processing within the timeout.");
    }

    private sealed record ModelRow(bool Ready, string Version, string? Method, string Notice);

    private sealed record PredictionRow(
        string Severity,
        double Confidence,
        List<ScoreRow> Scores,
        string ModelVersion,
        string Method,
        string Notice);

    private sealed record ScoreRow(string Severity, double Probability);

    private sealed record ObservationRow(
        Guid Id,
        string Status,
        string Severity,
        OpinionRow? ModelSeverity);

    private sealed record OpinionRow(
        string Severity,
        double Confidence,
        string ModelVersion,
        bool DisagreesWithApplied);
}
