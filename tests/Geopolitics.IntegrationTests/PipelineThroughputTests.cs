using System.Diagnostics;
using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Drives a realistic volume of observations through the real pipeline and the real database.
/// <para>
/// This is a behaviour test with a timing floor, not a benchmark. What it is actually for is the
/// failures that only appear under volume and never in a ten-item fixture run: a correlator that
/// rescores every incident ever recorded, a per-item query that turns into a table scan, a save that
/// grows with the size of the table. Those show up here as a run that takes minutes instead of
/// seconds.
/// </para>
/// <para>
/// The asserted bound is deliberately loose — an order of magnitude above what the pipeline actually
/// does — because CI runners vary by far more than the margin a tight bound would need. A tight one
/// would fail on a noisy runner and teach everyone to ignore it, which is worse than not having it.
/// Measured figures are written to the test output rather than asserted on.
/// </para>
/// </summary>
public sealed class PipelineThroughputTests(ITestOutputHelper output)
{
    private const int ObservationCount = 400;

    /// <summary>
    /// Ceiling for the whole run. Generous on purpose; see the class summary. It exists to catch
    /// quadratic behaviour, which blows through any bound, not to police milliseconds.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task TheRealPipelineAbsorbsAFewHundredObservationsWithoutDegrading()
    {
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?> { ["Seed:Enabled"] = "false" });

        // A client is created so the host actually builds and the database is initialised, which is
        // the same startup path the application uses.
        using var client = factory.CreateClient();

        var ingestion = factory.Services.GetRequiredService<IObservationIngestionService>();
        var reader = factory.Services.GetRequiredService<IObservationQueueReader>();
        var writer = factory.Services.GetRequiredService<IObservationQueueWriter>();

        foreach (var envelope in GenerateEnvelopes(ObservationCount))
        {
            await ingestion.IngestAsync(envelope, CancellationToken.None);
        }

        writer.Complete();

        var firstHalf = new List<double>(ObservationCount / 2);
        var secondHalf = new List<double>(ObservationCount / 2);
        var processed = 0;
        var overall = Stopwatch.StartNew();

        await foreach (var envelope in reader.DequeueAllAsync(CancellationToken.None))
        {
            var startedAt = Stopwatch.GetTimestamp();

            // A scope per item, exactly as the background processor isolates each unit of work.
            await using var scope = factory.Services.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();
            await processor.ProcessAsync(envelope, CancellationToken.None);

            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            (processed < ObservationCount / 2 ? firstHalf : secondHalf).Add(elapsed);
            processed++;
        }

        overall.Stop();

        firstHalf.Sort();
        secondHalf.Sort();

        var firstMedian = Median(firstHalf);
        var secondMedian = Median(secondHalf);

        output.WriteLine($"Processed {processed} observations in {overall.Elapsed.TotalSeconds:F1}s "
            + $"({processed / overall.Elapsed.TotalSeconds:F0}/s).");
        output.WriteLine($"Median per item: first half {firstMedian:F1} ms, second half {secondMedian:F1} ms.");
        output.WriteLine($"p95 second half: {Percentile(secondHalf, 0.95):F1} ms.");

        Assert.Equal(ObservationCount, processed);
        Assert.True(
            overall.Elapsed < Budget,
            $"Processing {processed} observations took {overall.Elapsed.TotalSeconds:F1}s, over the {Budget.TotalSeconds:F0}s budget.");

        // The real assertion. Per-item cost must not grow with how much is already stored — which is
        // what a correlator scanning every incident, or an unindexed lookup, would do. Four times is
        // loose enough to absorb warm-up and scheduling noise and tight enough that quadratic
        // behaviour cannot hide inside it.
        Assert.True(
            secondMedian <= Math.Max(4 * firstMedian, 10),
            $"Per-item cost grew from {firstMedian:F1} ms to {secondMedian:F1} ms as the database filled, "
                + "which suggests a stage whose cost depends on how much is already stored.");
    }

    [Fact]
    public async Task CancellationStopsProcessingPromptlyAndLeavesNothingHalfCommitted()
    {
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?> { ["Seed:Enabled"] = "false" });
        using var client = factory.CreateClient();

        var ingestion = factory.Services.GetRequiredService<IObservationIngestionService>();
        var reader = factory.Services.GetRequiredService<IObservationQueueReader>();

        foreach (var envelope in GenerateEnvelopes(50))
        {
            await ingestion.IngestAsync(envelope, CancellationToken.None);
        }

        using var cancellation = new CancellationTokenSource();
        var processed = 0;
        var stopped = false;

        try
        {
            await foreach (var envelope in reader.DequeueAllAsync(cancellation.Token))
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();
                await processor.ProcessAsync(envelope, cancellation.Token);
                processed++;

                if (processed == 5)
                {
                    await cancellation.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The expected way out. Cancellation propagates rather than being swallowed into a
            // silent early return, which would look identical to the queue running dry.
            stopped = true;
        }

        Assert.True(stopped, "Cancellation should surface as OperationCanceledException, not as a quiet stop.");

        // Well short of the 50 queued: the point is that cancellation took effect rather than being
        // noticed only once the queue emptied.
        Assert.InRange(processed, 5, 20);

        // Whatever was processed before the stop is fully committed and readable. A cancelled run
        // must leave complete records, not partial ones.
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var queries = verifyScope.ServiceProvider.GetRequiredService<IObservationQueryService>();
        var stored = await queries.ListRecentAsync(100, CancellationToken.None);

        Assert.Equal(processed, stored.Count);
        Assert.All(stored, observation =>
        {
            Assert.Equal(ObservationStatus.Persisted, observation.Status);
            Assert.NotNull(observation.IncidentId);
        });
    }

    /// <summary>
    /// Varied enough that deduplication does not collapse the run into a single stored observation,
    /// and varied enough across categories that correlation has real work to do rather than
    /// dropping everything into one incident.
    /// </summary>
    private static IEnumerable<ObservationEnvelope> GenerateEnvelopes(int count)
    {
        var kinds = new[] { ObservationKind.News, ObservationKind.Satellite, ObservationKind.ExternalEvent };
        var places = new[] { "Red Sea", "Gulf of Aden", "Strait of Hormuz", "Bab-el-Mandeb", "Black Sea", "Taiwan Strait" };
        var subjects = new[]
        {
            "A vessel reported an approach by small craft",
            "Naval escort activity was recorded",
            "An explosion damaged a tanker hull",
            "A protest blocked the port approach road",
            "A cyber intrusion was detected at the terminal operator",
            "Troop movements were observed near the coast",
        };

        for (var index = 0; index < count; index++)
        {
            yield return new ObservationEnvelope
            {
                SourceName = $"load:{index % 5}",
                Kind = kinds[index % kinds.Length],
                Title = $"{subjects[index % subjects.Length]} ({index})",
                Content = $"{subjects[index % subjects.Length]} near {places[index % places.Length]}. "
                    + $"Report reference {index}, filed by desk {index % 7}. No further detail was provided.",
                DeclaredLocationName = places[index % places.Length],

                // Spread across two days so correlation windows and analytics buckets both see a
                // realistic distribution rather than one instant.
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-(index % 2880)),
                Provenance = ObservationProvenance.Recorded,
            };
        }
    }

    private static double Median(List<double> sorted) =>
        sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
