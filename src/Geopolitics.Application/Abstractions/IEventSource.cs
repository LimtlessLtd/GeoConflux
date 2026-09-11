using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// A stream of observations from one ingestion adapter. Sources push into the queue and know
/// nothing about enrichment, persistence, or realtime delivery.
/// </summary>
public interface IEventSource
{
    /// <summary>Identifier used in logs, metrics, and queue health reporting.</summary>
    string Name { get; }

    /// <summary>
    /// Produces envelopes until the source is exhausted or cancellation is requested. Implementations
    /// must honour <paramref name="cancellationToken"/> promptly so shutdown stays graceful.
    /// </summary>
    IAsyncEnumerable<ObservationEnvelope> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A source that can also produce exactly one batch and stop.
/// <para>
/// <see cref="IEventSource.ReadAsync"/> is a stream, and for a polling adapter it is an endless one:
/// it fetches, yields, waits out the poll interval, and fetches again for as long as the host lives.
/// That is correct for a running host and useless to a caller that needs a bounded read — the
/// snapshot export in particular, which has to finish and write a file.
/// </para>
/// <para>
/// Separating the two is what stops that caller from having to guess. Draining the stream works for
/// a recorded source because it genuinely ends; doing the same to a live adapter hangs forever, and
/// nothing in the type system said so until this interface existed.
/// </para>
/// </summary>
public interface IBatchEventSource
{
    /// <summary>
    /// Asks the source for current data once. Returns an empty batch rather than throwing when the
    /// source is disabled or its provider could not be reached, matching how a failed poll behaves
    /// inside the continuous stream.
    /// </summary>
    Task<IReadOnlyList<ObservationEnvelope>> ReadBatchAsync(CancellationToken cancellationToken);
}
