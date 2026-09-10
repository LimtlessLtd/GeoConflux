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
