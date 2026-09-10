using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Workers;

/// <summary>
/// Runs the pipeline to completion and writes its output as static JSON.
/// <para>
/// This exists so the dashboard can be published to a static host, where no .NET process, database,
/// or realtime hub can run. The exported files are the genuine output of the real pipeline — the same
/// queue, normaliser, deduplicator, gazetteer, correlator, and persistence the application uses — so
/// the published site shows what the system actually produced rather than hand-written fixtures.
/// </para>
/// <para>
/// It drives the queue and processor directly instead of relying on the hosted services. Those exist
/// to run indefinitely and have no notion of "the recorded stream has finished and drained", which is
/// exactly the signal an export needs. Driving them here keeps the export deterministic and free of
/// timing guesses, while still exercising the real queue.
/// </para>
/// </summary>
public static partial class SnapshotExporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(IServiceProvider services, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SnapshotExporter));
        var sources = services.GetServices<IEventSource>().ToArray();

        if (sources.Length == 0)
        {
            LogNoSources(logger);
            return 1;
        }

        var ingestion = services.GetRequiredService<IObservationIngestionService>();
        var queue = services.GetRequiredService<IObservationQueueWriter>();
        var reader = services.GetRequiredService<IObservationQueueReader>();

        var queued = 0;

        foreach (var source in sources)
        {
            await foreach (var envelope in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
            {
                var result = await ingestion.IngestAsync(envelope, cancellationToken);

                if (result.Accepted)
                {
                    queued++;
                }
                else
                {
                    LogRejected(logger, source.Name, result.RejectionReason ?? "unspecified");
                }
            }
        }

        // Closing the queue is what lets the drain loop below terminate rather than wait for more.
        queue.Complete();

        var processed = 0;

        await foreach (var envelope in reader.DequeueAllAsync(cancellationToken))
        {
            // A scope per item mirrors how the background processor isolates each unit of work.
            await using var scope = services.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();
            await processor.ProcessAsync(envelope, cancellationToken);
            processed++;
        }

        LogProcessed(logger, queued, processed);

        await WriteAsync(services, outputDirectory, processed, cancellationToken);

        var resolvedDirectory = Path.GetFullPath(outputDirectory);
        LogExported(logger, resolvedDirectory);
        return 0;
    }

    private static async Task WriteAsync(
        IServiceProvider services,
        string outputDirectory,
        int processed,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var incidentQueries = scope.ServiceProvider.GetRequiredService<IIncidentQueryService>();
        var observationQueries = scope.ServiceProvider.GetRequiredService<IObservationQueryService>();

        var incidents = await incidentQueries.ListAsync(new IncidentSearch(250), cancellationToken);
        var observations = await observationQueries.ListRecentAsync(200, cancellationToken);

        Directory.CreateDirectory(outputDirectory);
        var evidenceDirectory = Path.Combine(outputDirectory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);

        await WriteJsonAsync(Path.Combine(outputDirectory, "incidents.json"), incidents, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputDirectory, "observations.json"), observations, cancellationToken);

        // One file per incident so the client can fetch evidence on demand, exactly as it does from
        // the by-incident endpoint when a backend is present.
        foreach (var incident in incidents)
        {
            var evidence = await observationQueries.ListByIncidentAsync(incident.Id, cancellationToken);
            await WriteJsonAsync(Path.Combine(evidenceDirectory, $"{incident.Id}.json"), evidence, cancellationToken);
        }

        var meta = new SnapshotMeta(
            GeneratedAt: DateTimeOffset.UtcNow,
            IncidentCount: incidents.Count,
            ObservationCount: observations.Count,
            ProcessedCount: processed,
            DuplicateCount: observations.Count(value => value.Status == Domain.ObservationStatus.Duplicate),
            UnresolvedLocationCount: observations.Count(value => value.Location is null),
            CorrelatedIncidentCount: incidents.Count(value => value.ObservationCount > 1),
            IsDemoData: true,
            Notice: "Synthetic replay data produced by a real run of the GeoConflux pipeline. "
                + "It is not live reporting and describes no real-world events.");

        await WriteJsonAsync(Path.Combine(outputDirectory, "meta.json"), meta, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
    }

    /// <param name="GeneratedAt">When this snapshot was produced, shown in the UI so its age is visible.</param>
    /// <param name="IsDemoData">Always true here; the exporter only ever runs against replay sources.</param>
    /// <param name="Notice">Plain-language provenance statement carried with the data itself.</param>
    private sealed record SnapshotMeta(
        DateTimeOffset GeneratedAt,
        int IncidentCount,
        int ObservationCount,
        int ProcessedCount,
        int DuplicateCount,
        int UnresolvedLocationCount,
        int CorrelatedIncidentCount,
        bool IsDemoData,
        string Notice);

    [LoggerMessage(Level = LogLevel.Error, Message = "No ingestion sources are registered, so there is nothing to export.")]
    private static partial void LogNoSources(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceName} produced an envelope that failed validation: {Reason}")]
    private static partial void LogRejected(ILogger logger, string sourceName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Queued} envelope(s) and processed {Processed}.")]
    private static partial void LogProcessed(ILogger logger, int queued, int processed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot written to {OutputDirectory}.")]
    private static partial void LogExported(ILogger logger, string outputDirectory);
}
