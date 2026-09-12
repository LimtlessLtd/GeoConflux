using System.Net;
using System.Net.Http.Json;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The limits on the one endpoint an unauthenticated caller can write to.
/// <para>
/// This project deliberately ships no identity system, which the specification permits. That choice
/// is only defensible if the open write path cannot be used to exhaust the process, so these are the
/// tests that make the choice defensible rather than merely convenient.
/// </para>
/// </summary>
public sealed class SubmissionProtectionTests
{
    private static object Submission(string content) => new
    {
        sourceName = "integration-test",
        title = "Test submission",
        content,
    };

    [Fact]
    public async Task ABurstOfSubmissionsFromOneClientIsRateLimited()
    {
        // Sources off, processor on, so submissions are the only thing in the pipeline and each one
        // is drained rather than filling the queue and colliding with the saturation test below.
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        // One more than the window allows. The limit itself is not asserted as a number here, only
        // that a burst is eventually refused: pinning the exact permit count in a test would make
        // tuning it a test change rather than a configuration change.
        for (var attempt = 0; attempt < 35; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/observations",
                Submission($"A test report, attempt {attempt}."));

            statuses.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // The caller is told when to come back, not merely that it was refused.
                Assert.True(
                    response.Headers.RetryAfter is not null,
                    "A rate-limited response should carry Retry-After.");
                break;
            }
        }

        Assert.Contains(HttpStatusCode.Accepted, statuses);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task ASubmissionBeyondTheContentCapIsRefusedRatherThanQueued()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/observations",
            Submission(new string('x', 100_000)));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The reason is stated rather than left as a bare 400, because a caller that does not know
        // which rule it broke will simply retry the same payload.
        Assert.Contains("20000 character limit", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullQueueAnswersTheCallerInsteadOfHoldingTheRequestOpen()
    {
        // A queue of one with nothing draining it. The second submission has nowhere to go, which is
        // precisely the state where the unbounded wait would have parked the request indefinitely.
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            new Dictionary<string, string?> { ["Pipeline:QueueCapacity"] = "1" });

        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync("/api/observations", Submission("The first report."));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await client.PostAsJsonAsync("/api/observations", Submission("The second report."));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
    }
}
