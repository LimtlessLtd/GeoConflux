using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The whole path from ordinary prose to an assessment, through the real composed application.
/// <para>
/// This is the demonstration that the feature works without a dataset credential, and it lives in
/// the test suite rather than in the published demo data on purpose. The recorded replay stream is
/// deliberately low-stakes — shipping incidents and generic events — and a fabricated claim that
/// somebody captured a real city is a qualitatively stronger artefact than anything else in it, even
/// labelled. So the capability ships dormant, exactly as the dataset adapters do, and is proved here.
/// </para>
/// </summary>
public sealed class ControlFromProseTests
{
    private const string Place = "Kharkiv";

    private const string Actor = "Northern Coalition Forces";

    [Fact]
    public async Task TwoIndependentCaptureClaimsBecomeAnAssessment()
    {
        using var factory = new PipelineFactory(
            runPipeline: true,
            runSources: true,
            settings: null,
            configureServices: services => services.AddSingleton<IEventSource>(new ClaimSource(
                [
                    ("wire-service-alpha", $"{Actor} captured {Place} after a night of fighting."),
                    ("wire-service-beta", $"{Actor} seized the city of {Place}, two residents said."),
                ])));

        using var client = factory.CreateClient();

        await WaitForControlEvidenceAsync(factory, expected: 2);

        var report = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/control", UriKind.Relative), CancellationToken.None);

        var place = Assert.Single(report.GetProperty("places").EnumerateArray());

        Assert.Equal(Place, place.GetProperty("place").GetString());
        Assert.Equal("Assessed", place.GetProperty("verdict").GetString());

        // A substring rather than the whole name, and the reason is worth recording: the actor is
        // whatever entity extraction produced, not what the detector saw. The offline stand-in reads
        // capitalised runs and returns "Coalition Forces" for text that says "Northern Coalition
        // Forces", so the assertion pins the substance — the party is the armed group rather than a
        // person, a place, or nothing — without pinning a word boundary that belongs to a different
        // component. It is also why an assessment publishes its evidence: the name it shows is the
        // source's wording as this system read it, and a reader can check the reading.
        Assert.Contains("Coalition Forces", place.GetProperty("actor").GetString(), StringComparison.Ordinal);

        // Two distinct sources, which is what let it assert at all: one channel saying a town has
        // fallen is evidence that a channel said so.
        Assert.Equal(2, place.GetProperty("sourceCount").GetInt32());

        // And the evidence travels with it. Identifiers rather than a count is the whole product
        // argument — an assessment a reader cannot trace back to records is a guess wearing one's
        // clothes.
        var evidence = place.GetProperty("evidence").EnumerateArray().ToList();
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, item => Assert.NotEqual(Guid.Empty, item.GetProperty("observationId").GetGuid()));
        Assert.All(evidence, item => Assert.Equal("Claimed", item.GetProperty("basis").GetString()));
    }

    [Fact]
    public async Task OneClaimOnItsOwnIsStoredAndAssertsNothing()
    {
        using var factory = new PipelineFactory(
            runPipeline: true,
            runSources: true,
            settings: null,
            configureServices: services => services.AddSingleton<IEventSource>(new ClaimSource(
                [("wire-service-alpha", $"{Actor} captured {Place} after a night of fighting.")])));

        using var client = factory.CreateClient();

        await WaitForControlEvidenceAsync(factory, expected: 1);

        var report = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/control", UriKind.Relative), CancellationToken.None);

        var place = Assert.Single(report.GetProperty("places").EnumerateArray());

        // Shown, with its evidence, and asserting nobody. The record is kept; the conclusion is not
        // drawn — which is the corroboration gate's shape applied to control.
        Assert.Equal("Insufficient", place.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, place.GetProperty("actor").ValueKind);
        Assert.Equal(0, report.GetProperty("placesAssessed").GetInt32());
    }

    [Fact]
    public async Task AReportOfFightingProducesNoControlEvidenceAtAll()
    {
        // The distinction the whole design turns on. An actor fighting at a place is evidence about
        // that place and is not a claim to hold it.
        using var factory = new PipelineFactory(
            runPipeline: true,
            runSources: true,
            settings: null,
            configureServices: services => services.AddSingleton<IEventSource>(new ClaimSource(
                [
                    ("wire-service-alpha", $"Heavy shelling was reported in {Place} overnight by {Actor}."),
                    ("wire-service-beta", $"{Actor} captured a soldier near {Place}."),
                ])));

        using var client = factory.CreateClient();
        await WaitForObservationsAsync(factory, expected: 2);

        var report = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/control", UriKind.Relative), CancellationToken.None);

        Assert.Empty(report.GetProperty("places").EnumerateArray());
        Assert.Contains(
            "not about the world",
            report.GetProperty("note").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits on a determinate condition — the rows carrying control evidence existing — rather than
    /// on the pipeline going quiet, which is a heuristic that races.
    /// </summary>
    private static Task WaitForControlEvidenceAsync(PipelineFactory factory, int expected) =>
        WaitAsync(
            factory,
            database => database.Observations.CountAsync(
                observation => observation.ControlSignal != ControlSignal.None, CancellationToken.None),
            expected,
            "observations carrying a control signal");

    private static Task WaitForObservationsAsync(PipelineFactory factory, int expected) =>
        WaitAsync(
            factory,
            database => database.Observations.CountAsync(CancellationToken.None),
            expected,
            "processed observations");

    private static async Task WaitAsync(
        PipelineFactory factory,
        Func<GeopoliticsDbContext, Task<int>> count,
        int expected,
        string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

            if (await count(database) >= expected)
            {
                return;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        Assert.Fail($"the pipeline did not produce {expected} {what} within two minutes");
    }

    /// <summary>A finite source of claims, one per configured line.</summary>
    private sealed class ClaimSource(IReadOnlyList<(string Source, string Text)> claims) : IEventSource
    {
        public string Name => "control-claims";

        public async IAsyncEnumerable<ObservationEnvelope> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (source, text) in claims)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new ObservationEnvelope
                {
                    SourceName = source,
                    Kind = ObservationKind.News,
                    SourceIdentifier = $"{source}-{text.GetHashCode(StringComparison.Ordinal)}",
                    Title = text,
                    Content = text,
                    DeclaredLocationName = Place,
                    OccurredAt = DateTimeOffset.UtcNow.AddHours(-2),
                    Provenance = ObservationProvenance.Polled,
                };

                await Task.Yield();
            }
        }
    }
}
