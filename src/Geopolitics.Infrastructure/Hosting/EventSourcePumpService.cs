using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Hosting;

/// <summary>
/// Runs every registered ingestion source concurrently and feeds what they produce into the queue.
/// <para>
/// Each source is isolated: one throwing or hanging must not stop the others, so a failure is logged
/// against that source and the remaining sources keep running. The queue outlives the sources: it is
/// completed on shutdown, not when a finite source runs out, so the manual submission endpoint keeps
/// working for the whole life of the host.
/// </para>
/// </summary>
public sealed partial class EventSourcePumpService(
    IEnumerable<IEventSource> sources,
    IObservationIngestionService ingestionService,
    IObservationQueueWriter queue,
    IOptions<PipelineOptions> options,
    ILogger<EventSourcePumpService> logger) : BackgroundService
{
    private readonly PipelineOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SourcesEnabled)
        {
            LogSourcesDisabled(logger);
            return;
        }

        var active = sources.ToArray();

        if (active.Length == 0)
        {
            LogNoSources(logger);
            return;
        }

        LogStarting(logger, active.Length);

        await Task.WhenAll(active.Select(source => PumpAsync(source, stoppingToken)));

        // The queue is deliberately left open. Sources are finite — the recorded replay stream ends —
        // but manual submissions arrive through the API for as long as the host is running, so
        // completing the queue here would silently break that endpoint. Completion belongs to
        // shutdown, which is what StopAsync handles.
        LogSourcesFinished(logger);
    }

    /// <summary>
    /// Closes the queue on shutdown so the processor can drain what is left and exit, rather than
    /// waiting on a writer that will never produce again.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken);
        LogStopped(logger);
    }

    private async Task PumpAsync(IEventSource source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
            {
                var result = await ingestionService.IngestAsync(envelope, cancellationToken);

                if (!result.Accepted)
                {
                    LogEnvelopeRejected(logger, source.Name, result.RejectionReason ?? "unspecified");
                }
            }

            LogSourceCompleted(logger, source.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogSourceCancelled(logger, source.Name);
        }
        catch (Exception exception)
        {
            // Contained deliberately: a broken adapter degrades coverage, it does not take the host down.
            LogSourceFailed(logger, exception, source.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion is disabled by configuration; no sources will run in this host.")]
    private static partial void LogSourcesDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No ingestion sources are registered; the pipeline will stay idle.")]
    private static partial void LogNoSources(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting {SourceCount} ingestion source(s).")]
    private static partial void LogStarting(ILogger logger, int sourceCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion source {SourceName} completed.")]
    private static partial void LogSourceCompleted(ILogger logger, string sourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion source {SourceName} stopped because the host is shutting down.")]
    private static partial void LogSourceCancelled(ILogger logger, string sourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion source {SourceName} failed and will not produce further observations. Other sources are unaffected.")]
    private static partial void LogSourceFailed(ILogger logger, Exception exception, string sourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceName} produced an envelope that failed validation: {Reason}")]
    private static partial void LogEnvelopeRejected(ILogger logger, string sourceName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "All ingestion sources have finished. The queue stays open for manual submissions.")]
    private static partial void LogSourcesFinished(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion stopped and the queue has been completed for shutdown.")]
    private static partial void LogStopped(ILogger logger);
}
