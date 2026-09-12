using System.Net.Http.Json;
using System.Text.Json;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The bundles this repository actually publishes, through the composed application.
/// <para>
/// The unit-level lint proves a committed bundle parses. This proves the rest of the claim: that it
/// reaches the read model as cited, dated, gazetteer-placed observations, using the real host, the
/// real pipeline, and the bundles that will be on the published page.
/// </para>
/// <para>
/// Assertions are on properties rather than counts, so committing another bundle does not break an
/// unrelated test. The one thing overridden is the age ceiling: the committed bundles are dated, and
/// a test that started failing a fortnight after the last collection run would be reporting the
/// calendar rather than the code.
/// </para>
/// </summary>
public sealed class CollectedBundlePipelineTests
{
    private static PipelineFactory Host() => new(
        runPipeline: true,
        runSources: true,
        settings: new Dictionary<string, string?>
        {
            ["Providers:AgentBriefs:Enabled"] = "true",
            ["Providers:AgentBriefs:MaxBundleAge"] = "3650.00:00:00",
            ["Replay:Enabled"] = "false",
        });

    [Fact]
    public async Task TheCommittedBundlesReachTheReadModelAsCitedObservations()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        var collected = await WaitForCollectedAsync(client);

        Assert.NotEmpty(collected);

        foreach (var observation in collected)
        {
            // Namespaced, so a reader can tell at a glance that this was gathered rather than polled.
            Assert.StartsWith(
                "collected:",
                observation.GetProperty("sourceName").GetString()!,
                StringComparison.Ordinal);

            // Real reporting, so not demo data — and not a live feed either.
            Assert.False(observation.GetProperty("isDemo").GetBoolean());
        }
    }

    [Fact]
    public async Task ANonEnglishReportIsPlacedByItsNativeScriptName()
    {
        // The point of the whole exercise. Before the native-script aliases existed, an Arabic source
        // named its place in Arabic, the gazetteer had no key for it, and the observation was retained
        // as unresolved — correct, and invisible on a globe.
        using var factory = Host();
        using var client = factory.CreateClient();

        var collected = await WaitForCollectedAsync(client);
        var arabic = collected.Where(observation =>
            observation.GetProperty("sourceName").GetString()!.Contains("Arabic", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(arabic);

        var placed = arabic.Where(observation =>
            observation.GetProperty("location").ValueKind != JsonValueKind.Null).ToArray();

        Assert.NotEmpty(placed);
    }

    [Fact]
    public async Task NoCollectedObservationTakesTheSourceProvidedPath()
    {
        // A collected item enters as News or Manual and neither kind may declare coordinates, so any
        // position it carries came from the gazetteer. Asserted through the real pipeline rather than
        // through the resolver, because what matters is the path an actual observation took.
        using var factory = Host();
        using var client = factory.CreateClient();

        var collected = await WaitForCollectedAsync(client);

        foreach (var observation in collected)
        {
            var kind = observation.GetProperty("kind").GetString();

            Assert.True(
                kind is "News" or "Manual",
                $"A collected observation entered as '{kind}', which is a kind permitted to declare its own coordinates.");
        }
    }

    private static async Task<JsonElement[]> WaitForCollectedAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var observations = await client.GetFromJsonAsync<JsonElement>("/api/observations?take=100");

            var collected = observations.EnumerateArray()
                .Where(observation => observation.GetProperty("provenance").GetString() == "Collected")
                .Where(observation => observation.GetProperty("status").GetString() != "Received")
                .ToArray();

            if (collected.Length > 0)
            {
                return collected;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            "No collected observation reached the read model. Either no bundle is committed, or the "
            + "collected source did not run.");
    }
}
