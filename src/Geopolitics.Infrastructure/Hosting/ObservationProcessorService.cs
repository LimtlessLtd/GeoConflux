using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Hosting;

/// <summary>
/// Drains the queue with a fixed number of concurrent workers.
/// <para>
/// Each envelope is processed inside its own DI scope so that every worker gets its own DbContext:
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> is not thread-safe, and sharing one across
/// workers is the classic way this design goes wrong. Concurrency is bounded by configuration rather
/// than by the queue, so a burst of ingestion cannot spawn unbounded database work.
/// </para>
/// </summary>
public sealed partial class ObservationProcessorService(
    IServiceScopeFactory scopeFactory,
    IObservationQueueReader queue,
    IOptions<PipelineOptions> options,
    ILogger<ObservationProcessorService> logger) : BackgroundService
{
    private readonly PipelineOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ProcessorEnabled)
        {
            LogProcessorDisabled(logger);
            return;
        }

        var workerCount = Math.Max(1, options.ProcessorConcurrency);
        LogStarting(logger, workerCount);

        var workers = Enumerable.Range(0, workerCount)
            .Select(index => Task.Run(() => RunWorkerAsync(index, stoppingToken), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers);
        LogStopped(logger);
    }

    private async Task RunWorkerAsync(int workerIndex, CancellationToken stoppingToken)
    {
        try
        {
            // Every worker reads the same channel; the channel itself does the load balancing.
            await foreach (var envelope in queue.DequeueAllAsync(stoppingToken).WithCancellation(stoppingToken))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IObservationProcessor>();
                var result = await processor.ProcessAsync(envelope, stoppingToken);

                if (result.Outcome == ProcessingOutcome.Failed)
                {
                    LogItemFailed(logger, workerIndex, envelope.SourceName, result.FailureReason ?? "unspecified");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogWorkerCancelled(logger, workerIndex);
        }
        catch (Exception exception)
        {
            // The processor already handles per-item failures; reaching here means the worker loop
            // itself broke, so it is logged loudly rather than silently ending the worker.
            LogWorkerFailed(logger, exception, workerIndex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue processing is disabled by configuration in this host.")]
    private static partial void LogProcessorDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Observation processor starting with {WorkerCount} worker(s).")]
    private static partial void LogStarting(ILogger logger, int workerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Observation processor stopped after draining the queue.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker {WorkerIndex} stopped because the host is shutting down.")]
    private static partial void LogWorkerCancelled(ILogger logger, int workerIndex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Worker {WorkerIndex} terminated unexpectedly.")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception, int workerIndex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Worker {WorkerIndex} could not process an observation from {SourceName}: {Reason}")]
    private static partial void LogItemFailed(ILogger logger, int workerIndex, string sourceName, string reason);
}
