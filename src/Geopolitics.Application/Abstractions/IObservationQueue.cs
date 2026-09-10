using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// Write side of the processing queue. Ingestion adapters and the observation API depend on this
/// alone, which keeps them unaware of the queue implementation.
/// </summary>
public interface IObservationQueueWriter
{
    /// <summary>
    /// Enqueues an envelope, waiting when the queue is full so that a fast producer applies
    /// backpressure instead of exhausting memory.
    /// </summary>
    ValueTask EnqueueAsync(ObservationEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Signals that no further envelopes will be written, allowing readers to drain and exit.
    /// </summary>
    void Complete();
}

/// <summary>Read side of the processing queue, consumed by the background processor.</summary>
public interface IObservationQueueReader
{
    IAsyncEnumerable<ObservationEnvelope> DequeueAllAsync(CancellationToken cancellationToken);
}

/// <summary>Point-in-time queue health, surfaced through health checks and metrics.</summary>
public interface IObservationQueueMonitor
{
    /// <summary>Number of envelopes waiting to be processed.</summary>
    int Depth { get; }

    /// <summary>Configured maximum depth before writers are made to wait.</summary>
    int Capacity { get; }

    /// <summary>Total envelopes accepted since process start.</summary>
    long TotalEnqueued { get; }
}
