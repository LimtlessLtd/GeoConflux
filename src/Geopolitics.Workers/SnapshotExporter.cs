using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Application.Conflicts;
using Geopolitics.Application.Coverage;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Control;
using Geopolitics.Application.Operations;
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

        // Draining starts before filling, and that ordering is the whole of the fix.
        //
        // The queue is bounded and blocks its producer when full, per ADR 003. That is right for a
        // running host, where a consumer is always there to relieve it. This exporter used to fill
        // the queue to completion and only then begin draining, which works for exactly as long as a
        // whole run fits inside the queue and deadlocks permanently the moment it does not. Four RSS
        // feeds fit inside 512 slots. A dataset adapter paging through global conflict history does
        // not, and the failure would not have been a slow export or a truncated one — it would have
        // been a build that hung until the runner timed it out.
        var drain = Task.Run(
            () => DrainAsync(services, reader, logger, cancellationToken),
            CancellationToken.None);

        var queued = 0;

        foreach (var source in sources)
        {
            // A batch source is read once; a stream source is drained. The distinction is not
            // cosmetic: a polling adapter's stream never ends, so draining one here would hang the
            // export forever rather than write a snapshot.
            var envelopes = source is IBatchEventSource batch
                ? await batch.ReadBatchAsync(cancellationToken)
                : await ReadToEndAsync(source, cancellationToken);

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

        // Closing the queue is what lets the drain loop terminate rather than wait for more.
        queue.Complete();

        var processed = await drain;

        LogProcessed(logger, queued, processed);

        await WriteAsync(services, outputDirectory, processed, cancellationToken);

        var resolvedDirectory = Path.GetFullPath(outputDirectory);
        LogExported(logger, resolvedDirectory);
        return 0;
    }

    /// <summary>
    /// Consumes the queue until it is completed, and reports how many items it processed.
    /// <para>
    /// Deliberately one consumer rather than the several the hosted processor runs. Correlation
    /// depends on the order records arrive in — which observation reaches an empty database first is
    /// what decides which one opens an incident — so a concurrent drain would make the published
    /// snapshot differ between runs of identical input. Throughput is not the constraint here; a
    /// reproducible build is.
    /// </para>
    /// <para>
    /// A failing item must not end the loop. The producer above is blocked on a bounded queue, so a
    /// consumer that dies on one bad record does not merely lose that record: it strands the
    /// producer, and the export hangs instead of failing. Each item is therefore contained, exactly
    /// as a failed poll is contained inside a polling source.
    /// </para>
    /// </summary>
    private static async Task<int> DrainAsync(
        IServiceProvider services,
        IObservationQueueReader reader,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processed = 0;

        await foreach (var envelope in reader.DequeueAllAsync(cancellationToken))
        {
            try
            {
                // A scope per item mirrors how the background processor isolates each unit of work.
                await using var scope = services.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();
                await processor.ProcessAsync(envelope, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogProcessingFailed(logger, exception, envelope.SourceName);
                continue;
            }

            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Reads a finite source to exhaustion. Only used for sources that genuinely end, such as a
    /// collection bundle read from disk.
    /// </summary>
    private static async Task<IReadOnlyList<ObservationEnvelope>> ReadToEndAsync(
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

        // Raised from 250 and 200 on 2026-09-14, when the deploy went from four English feeds to ten
        // across five languages. A run now produces around two hundred observations, and the old
        // ceiling would have quietly dropped the tail — publishing a sample of what the pipeline
        // collected while every count beside it described the whole. A truncating export is the same
        // defect as a truncating spatial search: not wrong output, but output that says less than it
        // appears to and does not admit it.
        //
        // This is a ceiling rather than a target. When a run genuinely approaches it the answer is
        // spatial tiling, which the global coverage assessment records as Sprint 14's problem, not a
        // larger number here.
        var incidents = await incidentQueries.ListAsync(new IncidentSearch(400), cancellationToken);
        var observations = await observationQueries.ListRecentAsync(400, cancellationToken);

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

        // The per-theatre statement of what this run managed to place, and at what precision. It is
        // exported alongside the data rather than derived in the browser because the honest version
        // of it needs the whole database, not the two hundred records the page happens to load.
        var coverage = await scope.ServiceProvider.GetRequiredService<ICoverageService>()
            .BuildAsync(cancellationToken);

        await WriteJsonAsync(Path.Combine(outputDirectory, "coverage.json"), coverage, cancellationToken);

        // What the run itself held, exported for the same reason coverage is: the page has no
        // backend to ask. On a build this describes a database that was created minutes ago and is
        // deleted with the job, and the report says so rather than being dressed up as the state of
        // a long-running host - the growth projection declines outright below a week of records.
        // That refusal is the panel's most useful output here, because it is the difference between
        // the two deployments stated by the data rather than by a caption.
        var operations = await scope.ServiceProvider.GetRequiredService<IOperationsService>()
            .BuildAsync(cancellationToken);

        await WriteJsonAsync(Path.Combine(outputDirectory, "operations.json"), operations, cancellationToken);

        // Who is assessed to hold what. Exported whole rather than summarised, because the evidence
        // identifiers are the point: an assessment a reader cannot trace back to records is the one
        // artefact this repository refuses to publish, and stripping them for the static build would
        // publish exactly that.
        var control = await scope.ServiceProvider.GetRequiredService<IControlAssessmentService>()
            .BuildAsync(cancellationToken);

        await WriteJsonAsync(Path.Combine(outputDirectory, "control.json"), control, cancellationToken);

        // What this run saw about each conflict, per window, for the same reason analytics are
        // exported per window: the published page has no backend to ask. Narratives are deliberately
        // not exported. They cost a model call each, most of them would be a refusal because the
        // snapshot's evidence is thin, and a refusal computed at build time and served for days
        // would read as a statement about the conflict rather than about one run.
        var conflictsDirectory = Path.Combine(outputDirectory, "conflicts");
        Directory.CreateDirectory(conflictsDirectory);

        var conflictActivity = scope.ServiceProvider.GetRequiredService<IConflictActivityService>();

        foreach (var window in AnalyticsWindow.All)
        {
            var report = await conflictActivity.BuildAsync(window, cancellationToken);
            await WriteJsonAsync(Path.Combine(conflictsDirectory, $"{window.Token}.json"), report, cancellationToken);
        }

        // Counted, never assumed. This exporter used to hard-code IsDemoData: true because the only
        // source it could read was the recorded stream. Now that live adapters can feed it, a fixed
        // label would be a claim about provenance the data does not support — in either direction:
        // stamping real reporting as synthetic is as wrong as the reverse.
        var demoObservations = observations.Count(value => value.Provenance == Domain.ObservationProvenance.Recorded);
        var polledObservations = observations.Count(value => value.Provenance == Domain.ObservationProvenance.Polled);
        var collectedObservations = observations.Count(value => value.Provenance == Domain.ObservationProvenance.Collected);

        // Live means real, which is polled and collected together. The two are then reported
        // separately, because "a feed carried it just now" and "an agent went and found it on
        // Tuesday" are both real and are not the same claim about freshness.
        var liveObservations = polledObservations + collectedObservations;

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
            PolledObservationCount: polledObservations,
            CollectedObservationCount: collectedObservations,
            DemoObservationCount: demoObservations,
            IsDemoData: liveObservations == 0,
            Notice: Describe(polledObservations, collectedObservations, demoObservations));

        await WriteJsonAsync(Path.Combine(outputDirectory, "meta.json"), meta, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
    }

    /// <param name="GeneratedAt">When this snapshot was produced, shown in the UI so its age is visible.</param>
    /// <param name="LiveObservationCount">Observations that are real reporting: polled and collected together.</param>
    /// <param name="PolledObservationCount">Observations a live adapter fetched during this run.</param>
    /// <param name="CollectedObservationCount">Observations an OSINT agent gathered into a recorded bundle.</param>
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
        int PolledObservationCount,
        int CollectedObservationCount,
        int DemoObservationCount,
        bool IsDemoData,
        string Notice);

    /// <summary>
    /// States the snapshot's provenance in plain language, naming each of the three intake paths
    /// that actually contributed.
    /// <para>
    /// A page carrying more than one kind has to say so. A single blanket label would be wrong about
    /// part of what it describes whichever label it chose, and the part it would be wrong about is
    /// the part a reader most needs to judge: whether what they are looking at is synthetic, fetched
    /// minutes ago, or gathered at some stated earlier moment.
    /// </para>
    /// </summary>
    private static string Describe(int polled, int collected, int demo)
    {
        if (polled + collected == 0)
        {
            // Reachable only if a run produced nothing real. There is no synthetic stream to
            // describe instead, and the deploy fails on an empty export rather than publishing
            // this, so it exists to be true rather than to be read (ADR 040).
            return "This run ingested no reporting. Nothing here is synthetic, because this system "
                + "contains no synthetic source; the snapshot is simply empty.";
        }

        var parts = new List<string>(3);

        if (polled > 0)
        {
            parts.Add($"{polled} ingested live from public feeds");
        }

        if (collected > 0)
        {
            parts.Add($"{collected} gathered into a recorded collection bundle by an OSINT agent, as of the collection time shown on each record");
        }

        if (demo > 0)
        {
            // Should be unreachable: nothing in the application can emit a synthetic observation.
            // Described rather than dropped, because a count that exists and is not shown is worse
            // than one that is — and the deploy fails the build on it besides.
            parts.Add($"{demo} synthetic, which should not be possible and is a defect");
        }

        return $"Observations in this snapshot: {string.Join("; ", parts)}. "
            + "Headlines and quotations are as published; the categories, severities, and "
            + "correlations shown beside them are this system's assessments, not the sources'. "
            + "Every record is individually labelled with where it came from.";
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "No ingestion sources are registered, so there is nothing to export.")]
    private static partial void LogNoSources(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceName} produced an envelope that failed validation: {Reason}")]
    private static partial void LogRejected(ILogger logger, string sourceName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source {SourceName} produced {Produced} envelope(s), {Accepted} accepted.")]
    private static partial void LogSourceRead(ILogger logger, string sourceName, int produced, int accepted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Queued} envelope(s) and processed {Processed}.")]
    private static partial void LogProcessed(ILogger logger, int queued, int processed);

    [LoggerMessage(Level = LogLevel.Error, Message = "An envelope from {SourceName} could not be processed and was dropped from this snapshot. The export continues.")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string sourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot written to {OutputDirectory}.")]
    private static partial void LogExported(ILogger logger, string outputDirectory);
}
