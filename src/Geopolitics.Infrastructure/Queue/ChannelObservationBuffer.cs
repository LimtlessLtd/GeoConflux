using System.Threading.Channels;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Queue;

/// <summary>
/// In-process queue backed by a bounded <see cref="Channel{T}"/>, per ADR 003.
/// <para>
/// The channel is bounded and uses <see cref="BoundedChannelFullMode.Wait"/>: when consumers fall
/// behind, producers block instead of the process accumulating unbounded work and failing on memory.
/// Losing queued items on restart is acceptable because sources are replayable and the durable
/// record is the database, not the queue.
/// </para>
/// </summary>
public sealed class ChannelObservationBuffer : IObservationQueueWriter, IObservationQueueReader, IObservationQueueMonitor
{
    private readonly Channel<ObservationEnvelope> channel;
    private long totalEnqueued;

    public ChannelObservationBuffer(IOptions<PipelineOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Capacity = Math.Max(1, options.Value.QueueCapacity);
        channel = Channel.CreateBounded<ObservationEnvelope>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public int Capacity { get; }

    public int Depth => channel.Reader.Count;

    public long TotalEnqueued => Interlocked.Read(ref totalEnqueued);

    public async ValueTask EnqueueAsync(ObservationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        await channel.Writer.WriteAsync(envelope, cancellationToken);
        Interlocked.Increment(ref totalEnqueued);
    }

    public void Complete() => channel.Writer.TryComplete();

    public IAsyncEnumerable<ObservationEnvelope> DequeueAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}
