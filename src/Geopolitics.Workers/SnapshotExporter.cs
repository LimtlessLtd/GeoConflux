using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
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
            // A batch source is read once; a stream source is drained. The distinction is not
            // cosmetic: a polling adapter's stream never ends, so draining one here would hang the
            // export forever rather than write a snapshot.
            var envelopes = source is IBatchEventSource batch
                ? await batch.ReadBatchAsync(cancellationToken)
                : await DrainAsync(source, cancellationToken);

            var accepted = 0;

            foreach (var envelope in envelopes)
            {
                var result = await ingestion.IngestAsync(envelope, cancellationToken);

                if (result.Accepted)
                {
                    queued++;
                    accepted++;
                }
                else
                {
                    LogRejected(logger, source.Name, result.RejectionReason ?? "unspecified");
                }
            }

            LogSourceRead(logger, source.Name, envelopes.Count, accepted);
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

    /// <summary>
    /// Reads a finite source to exhaustion. Only used for sources that genuinely end, such as the
    /// recorded replay stream.
    /// </summary>
    private static async Task<IReadOnlyList<ObservationEnvelope>> DrainAsync(
        IEventSource source,
        CancellationToken cancellationToken)
    {
        var envelopes = new List<ObservationEnvelope>();

        await foreach (var envelope in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            envelopes.Add(envelope);
        }

        return envelopes;
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
        var spatialQueries = scope.ServiceProvider.GetRequiredService<ISpatialQueryService>();
        var analyticsQueries = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();

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

        // Wide enough to cover everything the recorded stream produced, which all occurred within a
        // few hours of the run. A 24-hour window would make the published panel depend on how long
        // ago the build happened.
        var chokepoints = await spatialQueries.AnalyseChokepointsAsync(TimeSpan.FromDays(30), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputDirectory, "chokepoints.json"), chokepoints, cancellationToken);

        // Every window is exported rather than only the default, so the published page can switch
        // between them without a backend. They are computed at build time against the clock of the
        // run that produced them, which is why the page measures their age from the snapshot
        // timestamp rather than from the visitor's clock.
        var analyticsDirectory = Path.Combine(outputDirectory, "analytics");
        Directory.CreateDirectory(analyticsDirectory);

        foreach (var window in AnalyticsWindow.All)
        {
            var report = await analyticsQueries.BuildAsync(window, cancellationToken);
            await WriteJsonAsync(Path.Combine(analyticsDirectory, $"{window.Token}.json"), report, cancellationToken);
        }

        // Counted, never assumed. This exporter used to hard-code IsDemoData: true because the only
        // source it could read was the recorded stream. Now that live adapters can feed it, a fixed
        // label would be a claim about provenance the data does not support — in either direction:
        // stamping real reporting as synthetic is as wrong as the reverse.
        var demoObservations = observations.Count(value => value.IsDemo);
        var liveObservations = observations.Count - demoObservations;

        var meta = new SnapshotMeta(
            GeneratedAt: DateTimeOffset.UtcNow,
            IncidentCount: incidents.Count,
            ObservationCount: observations.Count,
            ProcessedCount: processed,
            DuplicateCount: observations.Count(value => value.Status == Domain.ObservationStatus.Duplicate),
            UnresolvedLocationCount: observations.Count(value => value.Location is null),
            CorrelatedIncidentCount: incidents.Count(value => value.ObservationCount > 1),
            ChokepointsWithActivity: chokepoints.Count(value => value.IncidentCount > 0),

            // Recorded so the published page can state how these distances were computed rather
            // than implying a precision the backend does not have.
            SpatialMethod: spatialQueries.Method,
            LiveObservationCount: liveObservations,
            DemoObservationCount: demoObservations,
            IsDemoData: liveObservations == 0,
            Notice: Describe(liveObservations, demoObservations));

        await WriteJsonAsync(Path.Combine(outputDirectory, "meta.json"), meta, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
    }

    /// <param name="GeneratedAt">When this snapshot was produced, shown in the UI so its age is visible.</param>
    /// <param name="LiveObservationCount">Observations that came from a real external source.</param>
    /// <param name="DemoObservationCount">Observations that came from the recorded stream.</param>
    /// <param name="IsDemoData">True only when nothing in this snapshot came from a live source.</param>
    /// <param name="Notice">Plain-language provenance statement carried with the data itself.</param>
    private sealed record SnapshotMeta(
        DateTimeOffset GeneratedAt,
        int IncidentCount,
        int ObservationCount,
        int ProcessedCount,
        int DuplicateCount,
        int UnresolvedLocationCount,
        int CorrelatedIncidentCount,
        int ChokepointsWithActivity,
        string SpatialMethod,
        int LiveObservationCount,
        int DemoObservationCount,
        bool IsDemoData,
        string Notice);

    /// <summary>
    /// States the snapshot's provenance in plain language, including the mixed case. A page carrying
    /// both real reporting and recorded demo records has to say so: a single blanket label would be
    /// wrong about half of what it describes whichever label it chose.
    /// </summary>
    private static string Describe(int live, int demo) => (live, demo) switch
    {
        (0, _) => "Synthetic replay data produced by a real run of the GeoConflux pipeline. "
            + "It is not live reporting and describes no real-world events.",
        (_, 0) => "Live reporting ingested from public news and humanitarian feeds by a real run of "
            + "the GeoConflux pipeline. Headlines are real; the categories, severities, and "
            + "correlations shown beside them are this system's assessments, not the publishers'.",
        _ => $"A mixed snapshot: {live} observation(s) ingested live from public feeds and {demo} "
            + "replayed from the recorded demo stream. Every record is individually labelled with "
            + "which it is.",
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "No ingestion sources are registered, so there is nothing to export.")]
    private static partial void LogNoSources(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceName} produced an envelope that failed validation: {Reason}")]
    private static partial void LogRejected(ILogger logger, string sourceName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source {SourceName} produced {Produced} envelope(s), {Accepted} accepted.")]
    private static partial void LogSourceRead(ILogger logger, string sourceName, int produced, int accepted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Queued} envelope(s) and processed {Processed}.")]
    private static partial void LogProcessed(ILogger logger, int queued, int processed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot written to {OutputDirectory}.")]
    private static partial void LogExported(ILogger logger, string outputDirectory);
}
