using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The Sprint 3 definition of done, driven through the real host: a manually submitted
/// foreign-language observation is enriched, schema-validated, located by a deterministic resolver,
/// persisted, and served with its provenance intact — all of it with no credentials configured.
/// </summary>
public sealed class EnrichmentPipelineTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The Arabic report used by both foreign-language tests, so they differ only by provider.</summary>
    private const string ArabicSubmission =
        "أفادت تقارير بأن سفينة شحن تعرضت لاقتراب قوارب صغيرة قرب باب المندب، ولم تقع إصابات.";

    [Fact]
    public async Task AForeignLanguageSubmissionIsEnrichedValidatedLocatedAndServed()
    {
        // Run against a provider that can actually read the text. The offline stand-in cannot, and
        // the test below asserts what it does instead.
        using var factory = new PipelineFactory(
            runPipeline: true,
            runSources: false,
            chatClient: new CapableChatClient());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new
            {
                sourceName = "analyst-desk",

                // No declared category, severity, coordinates, or place name. Everything the stored
                // record carries has to come from enrichment and deterministic resolution.
                content = ArabicSubmission,
            },
            Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = Assert.Single(await WaitForObservationsAsync(client, expected: 1));

        // Translated and classified, with the language of the original recorded alongside.
        Assert.StartsWith("ai:", observation.ClassificationMethod, StringComparison.Ordinal);
        Assert.Equal("ar", observation.DetectedLanguage);
        Assert.Equal(EventType.Piracy, observation.EventType);
        Assert.Equal(0.87, observation.ClassificationConfidence);
        Assert.StartsWith("A cargo vessel was approached", observation.Summary!, StringComparison.Ordinal);
        Assert.Equal("Meridian Shipping", Assert.Single(observation.Entities).Name);
        Assert.NotNull(observation.SeverityRationale);

        // Both texts are served, and the English is attributed. A reader can compare the translation
        // with what was actually published, which is the only thing that makes a translated claim
        // checkable rather than merely readable.
        Assert.Equal(TranslationState.MachineTranslated, observation.Translation);
        Assert.Equal("Cargo vessel approached by small craft near Bab-el-Mandeb", observation.TranslatedTitle);
        Assert.StartsWith("A cargo vessel was approached", observation.TranslatedSummary!, StringComparison.Ordinal);
        Assert.StartsWith("ai:", observation.TranslationMethod, StringComparison.Ordinal);
        Assert.Equal(ArabicSubmission, observation.OriginalContent);

        // The model named a place; only the deterministic resolver turned it into a position.
        Assert.Equal("Bab-el-Mandeb", observation.LocationName);
        Assert.NotNull(observation.Location);
        Assert.Equal(12.585, observation.Location.Latitude, precision: 3);
        Assert.Equal(43.334, observation.Location.Longitude, precision: 3);

        // And it opened no incident, because a submission through the open write path is an
        // anonymous claim. Every assertion above still holds, which is the property worth keeping
        // separate: the corroboration gate withholds the conclusion, not the enrichment.
        //
        // That an incident carries its observation's confidence forward is asserted against
        // published reporting instead, in CollectedBundlePipelineTests, where the record actually
        // has an organisation behind it.
        Assert.Null(observation.IncidentId);
        Assert.Equal(ObservationStatus.Uncorroborated, observation.Status);
    }

    [Fact]
    public async Task TheOfflineProviderDeclinesToClassifyTextItCannotReadAndSaysSo()
    {
        // The shipped stand-in matches English keywords. On an Arabic report it has nothing to go on,
        // reports low confidence, and is therefore not adopted — which is the behaviour that keeps a
        // credential-free demo from presenting a guess as an inference. The observation is still
        // stored, still located, and still visible.
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new { sourceName = "analyst-desk", content = ArabicSubmission, locationName = "Bab-el-Mandeb" },
            Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = Assert.Single(await WaitForObservationsAsync(client, expected: 1));

        // Held rather than persisted, because a submission through the open write path is an
        // anonymous claim and the corroboration gate does not let one open an incident. Everything
        // this test is actually about is unchanged by that: the model was called, its answer was
        // not adopted, and the observation is stored, classified and placed.
        Assert.Equal(ObservationStatus.Uncorroborated, observation.Status);
        Assert.Equal("keyword", observation.ClassificationMethod);
        Assert.NotNull(observation.Location);

        // And it says so about the translation too, rather than letting the Arabic language tag be
        // read downstream as evidence that a translation happened. The source text is served either
        // way, so a reader gets the report itself even when nothing could render it into English.
        Assert.Equal(TranslationState.NotTranslated, observation.Translation);
        Assert.Null(observation.TranslatedTitle);
        Assert.Equal(ArabicSubmission, observation.OriginalContent);

        // No incident, and the claim is still fully retained — which is the whole distinction
        // between holding a claim and dropping one.
        Assert.Null(observation.IncidentId);
    }

    [Fact]
    public async Task ASubmissionIsProcessedNormallyWhenEnrichmentIsDisabled()
    {
        // Turning the provider off is the operator's lever when a model is costing money or
        // misbehaving. It must degrade classification quality and nothing else.
        using var factory = new PipelineFactory(
            runPipeline: true,
            runSources: false,
            settings: new Dictionary<string, string?> { ["Enrichment:Enabled"] = "false" });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new
            {
                sourceName = "analyst-desk",
                content = "Pirates approached a cargo vessel near the Gulf of Aden and the crew was unharmed.",
                locationName = "Gulf of Aden",
            },
            Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = Assert.Single(await WaitForObservationsAsync(client, expected: 1));

        Assert.Equal(ObservationStatus.Uncorroborated, observation.Status);
        Assert.Equal(EventType.Piracy, observation.EventType);
        Assert.Equal("keyword", observation.ClassificationMethod);
        Assert.True(observation.ClassificationConfidence > 0);
        Assert.NotNull(observation.Location);
        Assert.Null(observation.DetectedLanguage);
    }

    [Fact]
    public async Task EveryObservationTheApiServesCarriesAStatedConfidenceAndMethod()
    {
        // The dashboard must never be able to show a category without saying how much to trust it,
        // so the contract is checked across the whole recorded stream rather than on one record.
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        var observations = await WaitForObservationsAsync(client, expected: 8);

        Assert.All(observations, observation =>
        {
            Assert.InRange(observation.ClassificationConfidence, 0, 1);
            Assert.False(string.IsNullOrWhiteSpace(observation.ClassificationMethod));
            Assert.NotEqual("none", observation.ClassificationMethod);
        });

        var incidents = await client.GetFromJsonAsync<List<IncidentResponse>>("/api/incidents?take=200", Json);
        Assert.NotNull(incidents);

        Assert.All(
            incidents.Where(incident => incident.ObservationCount > 0),
            incident =>
            {
                Assert.InRange(incident.ClassificationConfidence, 0.0001, 1);
                Assert.NotEqual("none", incident.ClassificationMethod);
            });
    }

    [Fact]
    public async Task NoServedRecordEverCarriesCoordinatesItWasNotGivenDeterministically()
    {
        // ADR 005 asserted against the served payload. A located observation must name a place the
        // gazetteer knows or have been handed coordinates by its source; nothing else may be placed.
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        var observations = await WaitForObservationsAsync(client, expected: 8);

        Assert.All(
            observations.Where(observation => observation.Location is not null),
            observation => Assert.False(string.IsNullOrWhiteSpace(observation.Location!.Name)));

        // At least one recorded report is deliberately unmappable, and it stays unmapped rather than
        // being given plausible-looking coordinates. Duplicates are excluded: they are stopped before
        // location resolution runs, so having no resolution note is correct for them rather than a gap.
        var unplaced = observations
            .Where(observation => observation.Location is null && observation.Status != ObservationStatus.Duplicate)
            .ToArray();

        Assert.NotEmpty(unplaced);
        Assert.All(unplaced, observation => Assert.False(string.IsNullOrWhiteSpace(observation.LocationResolutionNote)));
    }

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
