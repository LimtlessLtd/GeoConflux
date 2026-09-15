using System.Text.Json;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The export that builds the published dashboard, run against the real composed application.
/// <para>
/// Until now this code path had no test at all, which is an odd gap for the one piece of the system
/// whose output is the thing anybody actually looks at. It also hid a failure that only appears at
/// volume: the export fills a bounded queue and then drains it, and a run larger than the queue
/// therefore blocks forever with no consumer to relieve it. Four RSS feeds never came close. A
/// dataset adapter paging through conflict history does, which is why this is Sprint 11's problem
/// rather than a later one.
/// </para>
/// </summary>
public sealed class SnapshotExportTests
{
    /// <summary>
    /// Deliberately tiny, and the run below is deliberately several times it. Reproducing this with
    /// the shipped capacity of 512 would mean pushing well over five hundred records through a real
    /// database to prove a property that has nothing to do with the number — the property is
    /// "more than the queue holds", and shrinking the queue states it far more directly than
    /// inflating the run.
    /// </summary>
    private const int QueueCapacity = 8;

    private const int EnvelopeCount = 40;

    /// <summary>
    /// A deadline rather than an assertion after the fact. The regression this guards against is a
    /// hang, and a hung test that is eventually killed by the CI runner reports as a timeout on the
    /// whole job rather than as this test failing — so the export is given a token that expires and
    /// the failure arrives here, named, with the reason attached.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task AnExportLargerThanTheQueueCompletesInsteadOfDeadlocking()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"geoconflux-export-{Guid.NewGuid():N}");

        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: new Dictionary<string, string?>
            {
                ["Pipeline:QueueCapacity"] = QueueCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            configureServices: services =>
                services.AddSingleton<IEventSource>(new BulkEventSource("bulk", EnvelopeCount)));

        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(Deadline);

        try
        {
            var exitCode = await SnapshotExporter.RunAsync(factory.Services, directory, deadline.Token);
            Assert.Equal(0, exitCode);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                $"The export did not finish within {Deadline.TotalMinutes:F0} minutes for {EnvelopeCount} "
                    + $"envelopes against a queue of {QueueCapacity}. A bounded queue that is filled "
                    + "before anything drains it blocks its producer permanently.");
        }

        var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "meta.json"), CancellationToken.None));
        var processed = meta.RootElement.GetProperty("processedCount").GetInt32();

        // Every envelope reached the processor, not merely the queueful that fitted. A drain that
        // started after a partial fill would still terminate; it would just publish a truncated
        // snapshot, which is the quieter and more misleading of the two failures.
        Assert.Equal(EnvelopeCount, processed);

        // The published page carries what the run itself held, so the row counts on it describe a
        // database created for that build. Exporting it is what lets the page say so rather than
        // leaving a reader to assume a host that has been watching.
        var operations = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "operations.json"), CancellationToken.None));

        var observations = operations.RootElement
            .GetProperty("holdings").GetProperty("tables").EnumerateArray()
            .Single(table => table.GetProperty("table").GetString() == "observations")
            .GetProperty("rows").GetInt64();

        Assert.Equal(EnvelopeCount, observations);

        // The assessment is exported whole, evidence identifiers included. Summarising it for the
        // static build would publish an assessment a reader cannot trace back to records, which is
        // the one artefact this repository refuses.
        var control = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "control.json"), CancellationToken.None));

        Assert.True(control.RootElement.TryGetProperty("places", out _));
        Assert.Contains(
            "not a front line",
            control.RootElement.GetProperty("method").GetString(),
            StringComparison.Ordinal);

        Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// A source that hands over more than the queue can hold in one batch, which is what a dataset
    /// adapter does and what no feed adapter here has ever done.
    /// </summary>
    private sealed class BulkEventSource(string name, int count) : IEventSource, IBatchEventSource
    {
        public string Name => name;

        public Task<IReadOnlyList<ObservationEnvelope>> ReadBatchAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ObservationEnvelope>>([.. Envelopes()]);

        public async IAsyncEnumerable<ObservationEnvelope> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var envelope in Envelopes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
                await Task.Yield();
            }
        }

        private IEnumerable<ObservationEnvelope> Envelopes()
        {
            var places = new[] { "Kharkiv", "Aden", "Mekelle", "Hodeidah" };

            for (var index = 0; index < count; index++)
            {
                yield return new ObservationEnvelope
                {
                    SourceName = name,
                    Kind = ObservationKind.ExternalEvent,
                    SourceIdentifier = $"{name}-{index}",
                    Title = $"Coded event {index}",
                    Content = $"Armed clash recorded near {places[index % places.Length]}, record {index}.",
                    DeclaredLocationName = places[index % places.Length],
                    OccurredAt = DateTimeOffset.UtcNow.AddHours(-(index % 48)),
                    Provenance = ObservationProvenance.Polled,
                };
            }
        }
    }
}
