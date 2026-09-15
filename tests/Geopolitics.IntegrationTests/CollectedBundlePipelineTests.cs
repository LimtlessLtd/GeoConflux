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
    public async Task PublishedReportingOpensAnIncidentAndCarriesItsAssessmentForward()
    {
        // The other side of the corroboration gate. A collected document has a named organisation
        // behind it, so it stands on its own and needs nothing to agree with it — and the incident
        // it opens repeats the confidence and method the observation was assessed with, rather than
        // presenting a category the dashboard cannot say how much to trust.
        using var factory = Host();
        using var client = factory.CreateClient();

        var collected = await WaitForCollectedAsync(client);
        var documents = collected
            .Where(observation => observation.GetProperty("tier").GetString() == "Published")
            .ToArray();

        Assert.NotEmpty(documents);

        var correlated = documents
            .Where(observation => observation.GetProperty("incidentId").ValueKind != JsonValueKind.Null)
            .ToArray();

        Assert.NotEmpty(correlated);

        var observation = correlated[0];
        var incident = await client.GetFromJsonAsync<JsonElement>(
            $"/api/incidents/{observation.GetProperty("incidentId").GetGuid()}");

        Assert.Equal(
            observation.GetProperty("classificationConfidence").GetDouble(),
            incident.GetProperty("classificationConfidence").GetDouble(),
            precision: 6);
        Assert.Equal(
            observation.GetProperty("classificationMethod").GetString(),
            incident.GetProperty("classificationMethod").GetString());
    }

    [Fact]
    public async Task ACollectedPostEntersCitedToItsChannelAndCannotFormAnIncidentAlone()
    {
        // The sprint's definition of done, asserted against the bundles this repository actually
        // publishes rather than against a fixture. A Tier B item reaches the read model cited to the
        // channel that posted it, is distinguishable from wire reporting by something a renderer can
        // read, and has opened no incident on its own.
        using var factory = Host();
        using var client = factory.CreateClient();

        var collected = await WaitForCollectedAsync(client);
        var posts = collected
            .Where(observation => observation.GetProperty("tier").GetString() == "UserGenerated")
            .ToArray();

        Assert.NotEmpty(posts);

        foreach (var post in posts)
        {
            // Cited to a channel on a named platform, not to a publisher.
            var platform = post.GetProperty("platform").GetString();
            var channel = post.GetProperty("channel").GetString();

            Assert.False(string.IsNullOrWhiteSpace(platform));
            Assert.False(string.IsNullOrWhiteSpace(channel));
            Assert.Equal($"collected:{platform}/{channel}", post.GetProperty("sourceName").GetString());

            // Fully retained either way: stored, classified and shown. What is withheld is only the
            // assertion that the thing it describes happened.
            Assert.False(string.IsNullOrWhiteSpace(post.GetProperty("summary").GetString()));

            if (post.GetProperty("incidentId").ValueKind == JsonValueKind.Null)
            {
                Assert.Equal("Uncorroborated", post.GetProperty("status").GetString());
                continue;
            }

            // A post that does belong to an incident did not bring it into being by itself. This is
            // the rule stated exactly: not "a claim never reaches an incident", which would make the
            // gate a way of discarding the fastest reporting, but "a claim is never the whole of
            // one". The published bundle exercises both halves — three Hormuz posts from one channel
            // stay held, and a post the Tier A documents already account for is released into their
            // incident.
            var incident = await client.GetFromJsonAsync<JsonElement>(
                $"/api/incidents/{post.GetProperty("incidentId").GetGuid()}");

            Assert.True(
                incident.GetProperty("observationCount").GetInt32() > 1,
                "a user-generated claim is the only observation behind an incident");
        }
    }

    [Fact]
    public async Task ACollectedPostIsStillPlacedOnTheMapWhileItIsHeld()
    {
        // Held is not hidden, and this is the assertion that keeps it that way. A claim nobody can
        // see is a claim nobody can corroborate, and hiding claims would also conceal how much of
        // the picture rests on unsupported posts — which is the bias coverage exists to expose.
        using var factory = Host();
        using var client = factory.CreateClient();

        var collected = await WaitForCollectedAsync(client);
        var placed = collected
            .Where(observation => observation.GetProperty("tier").GetString() == "UserGenerated"
                && observation.GetProperty("location").ValueKind != JsonValueKind.Null)
            .ToArray();

        Assert.NotEmpty(placed);
    }

    [Fact]
    public async Task TheDashboardPublishesWhatEachSourceActuallyGave()
    {
        // The run's own account of itself, carried from the committed bundle to the API. The
        // entries that produced nothing are what this is for: without them a refused channel, a
        // channel that publishes nothing, a channel read that matched nothing and a channel the caps
        // emptied are one absence — and an absence reads as "nothing happened there".
        using var factory = Host();
        using var client = factory.CreateClient();

        await WaitForCollectedAsync(client);

        var coverage = await client.GetFromJsonAsync<JsonElement>("/api/analytics/coverage");
        var sources = coverage.GetProperty("sources").EnumerateArray().ToArray();

        Assert.NotEmpty(sources);
        Assert.Contains(sources, source => source.GetProperty("collected").GetInt32() > 0);

        // At least one source that was asked and gave nothing, stated as such rather than absent.
        Assert.Contains(sources, source => source.GetProperty("isEmpty").GetBoolean());
        Assert.All(sources, source =>
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("outcome").GetString())));

        // And the breadth the plan asks for, all four of them.
        Assert.NotEmpty(coverage.GetProperty("byRegion").EnumerateArray());
        Assert.NotEmpty(coverage.GetProperty("byLanguage").EnumerateArray());
        Assert.NotEmpty(coverage.GetProperty("byTier").EnumerateArray());
        Assert.NotEmpty(coverage.GetProperty("byPlatform").EnumerateArray());
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

    /// <summary>
    /// Waits until the pipeline has drained every collected observation, rather than until the first
    /// one arrives.
    /// <para>
    /// Returning on the first processed item raced the pipeline. A bundle's items are ingested and
    /// processed independently, so one could cross the line while the rest were still
    /// <c>Received</c> — and a caller looking for a particular item then searched a partial set. That
    /// passed on an idle developer machine and failed intermittently on a loaded CI runner, which is
    /// the worst form for a test to fail in.
    /// </para>
    /// <para>
    /// Two conditions have to hold together, because neither is sufficient alone. No collected
    /// observation may still be <c>Received</c>, which catches items the processor has not finished;
    /// and the total has to stop growing, which catches items the source has not yet read from disk.
    /// Both are count-free, so committing another bundle still does not break an unrelated test.
    /// </para>
    /// </summary>
    private static async Task<JsonElement[]> WaitForCollectedAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var enqueued = await TotalEnqueuedAsync(client);
            var observations = await client.GetFromJsonAsync<JsonElement>("/api/observations?take=100");

            var all = observations.EnumerateArray()
                .Where(observation => observation.GetProperty("provenance").GetString() == "Collected")
                .ToArray();

            var processed = all
                .Where(observation => observation.GetProperty("status").GetString() != "Received")
                .ToArray();

            // Every envelope the queue has accepted has become a row, and nothing is left waiting.
            // Replay is off in this host and the bundle source is the only producer, so the queue's
            // own counter is exactly the number of collected observations to expect — which makes
            // this a statement about the pipeline having finished rather than about it having been
            // quiet for a moment.
            if (enqueued > 0 && all.Length == enqueued && processed.Length == all.Length)
            {
                return processed;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            "No collected observation reached the read model, or the pipeline never settled. Either "
            + "no bundle is committed, or the collected source did not run.");
    }

    /// <summary>
    /// How many envelopes the processing queue has accepted, read from the health endpoint.
    /// <para>
    /// This replaced a settling heuristic — "everything visible has been processed, and the count has
    /// not moved for three polls" — that was wrong in a way only a loaded machine showed. The two
    /// committed bundles are split by tier: seven published documents in one file and six social
    /// posts in the other. A processor part-way through the batch satisfies both halves of that
    /// heuristic, because the items it has not reached yet are in the queue rather than in the
    /// database and are therefore invisible to it. The test then asserted against seven of thirteen
    /// records and found no platform among them, which is exactly what CI reported.
    /// </para>
    /// </summary>
    private static async Task<int> TotalEnqueuedAsync(HttpClient client)
    {
        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");

        foreach (var check in health.GetProperty("checks").EnumerateArray())
        {
            if (check.GetProperty("name").GetString() == "processing-queue"
                && check.GetProperty("data").TryGetProperty("totalEnqueued", out var total))
            {
                return total.GetInt32();
            }
        }

        return 0;
    }
}
