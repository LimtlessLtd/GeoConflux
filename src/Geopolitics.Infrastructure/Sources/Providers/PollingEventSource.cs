using System.Runtime.CompilerServices;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Shared behaviour for adapters that repeatedly ask an upstream provider what is new.
/// <para>
/// Each concrete adapter supplies only the part that is genuinely provider-specific: one fetch that
/// returns envelopes. Everything that must be true of <em>every</em> external integration lives here
/// so it cannot be forgotten in a new adapter. The poll loop honours cancellation, a failing poll is
/// contained rather than ending the source, latency and failures are measured per provider, and
/// repeats within a polling window are suppressed before they reach the queue.
/// </para>
/// </summary>
public abstract partial class PollingEventSource(
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger logger) : IEventSource
{
    /// <summary>
    /// How many recently emitted identifiers to remember. Sized to comfortably exceed a few polls'
    /// worth of items for a busy feed: large enough that a stable feed emits each item once, small
    /// enough that the set cannot grow without bound in a long-running host.
    /// </summary>
    private const int RecentIdentifierCapacity = 2_048;

    public abstract string Name { get; }

    /// <summary>Whether configuration permits this adapter to reach its provider at all.</summary>
    protected abstract bool IsEnabled { get; }

    protected abstract TimeSpan PollInterval { get; }

    protected ILogger Logger { get; } = logger;

    public async IAsyncEnumerable<ObservationEnvelope> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            // Not a warning. A disabled provider is the documented default, and logging it at
            // warning level would train readers to ignore the level that matters.
            LogDisabled(Logger, Name);
            yield break;
        }

        LogStarting(Logger, Name, PollInterval);

        // Scoped to this enumeration rather than held on the instance. These sources are singletons,
        // so instance state would be shared by any two concurrent readers with nothing protecting
        // it. Keeping the window local makes each run independent, and a restarted source
        // legitimately starts with a clean window.
        var recent = new RecentIdentifiers();

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var envelope in await PollOnceAsync(recent, cancellationToken))
            {
                yield return envelope;
            }

            try
            {
                await Task.Delay(PollInterval, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Asks the provider for current data. Implementations may throw: the caller treats a failed poll
    /// as a degraded cycle rather than a dead source.
    /// </summary>
    protected abstract Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one poll and returns what should be emitted.
    /// <para>
    /// Returns a materialised list rather than streaming because the failure containment below cannot
    /// wrap a <c>yield return</c>. Doing the fallible work here keeps the iterator above simple and
    /// keeps a provider fault from escaping as an exception that would kill the source.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ObservationEnvelope>> PollOnceAsync(
        RecentIdentifiers recent,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        IReadOnlyList<ObservationEnvelope> batch;

        try
        {
            batch = await FetchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception exception)
        {
            // Deliberately broad. An adapter reaching an external network can fail in ways no
            // exception list anticipates, and every one of them means the same thing here: this
            // cycle produced nothing, the next one will try again, and no other source is affected.
            RecordLatency(startedAt, "error");
            diagnostics.ProviderFailures.Add(1, new KeyValuePair<string, object?>("provider", Name));
            LogPollFailed(Logger, exception, Name, PollInterval);
            return [];
        }

        RecordLatency(startedAt, "ok");

        var fresh = new List<ObservationEnvelope>(batch.Count);
        var suppressed = 0;

        foreach (var envelope in batch)
        {
            if (recent.IsRepeat(envelope))
            {
                suppressed++;
                continue;
            }

            fresh.Add(envelope);
        }

        LogPolled(Logger, Name, batch.Count, fresh.Count, suppressed);
        return fresh;
    }

    private void RecordLatency(long startedAt, string outcome) =>
        diagnostics.ProviderLatency.Record(
            timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("provider", Name),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// A bounded, insertion-ordered window of what this run has already emitted.
    /// <para>
    /// The pipeline deduplicates by fingerprint against the database, so this is not what makes the
    /// system correct. It is what keeps a ten-minute poll of a slow-moving feed from re-enqueuing the
    /// same fifty items every cycle purely to have each one rejected downstream. Correctness stays at
    /// the fingerprint check; this only avoids paying for work whose outcome is already known.
    /// </para>
    /// </summary>
    private sealed class RecentIdentifiers
    {
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        private readonly Queue<string> order = new();

        public bool IsRepeat(ObservationEnvelope envelope)
        {
            // Without a provider identifier the content itself is the only stable key available.
            // The separator is a pipe rather than a space because source names are assigned by the
            // adapters in this repository and never contain one, which keeps the two halves of the
            // key from running together ambiguously.
            var key = string.IsNullOrWhiteSpace(envelope.SourceIdentifier)
                ? $"{envelope.SourceName}|{envelope.Content}"
                : $"{envelope.SourceName}|{envelope.SourceIdentifier}";

            if (!seen.Add(key))
            {
                return true;
            }

            order.Enqueue(key);

            // Oldest out first, so a long-running source evicts what it is least likely to see again.
            while (order.Count > RecentIdentifierCapacity)
            {
                seen.Remove(order.Dequeue());
            }

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider {ProviderName} is not enabled; it will make no external calls.")]
    private static partial void LogDisabled(ILogger logger, string providerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider {ProviderName} is live and will poll every {PollInterval}.")]
    private static partial void LogStarting(ILogger logger, string providerName, TimeSpan pollInterval);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Provider {ProviderName} returned {ReceivedCount} item(s): {EmittedCount} emitted, {SuppressedCount} already seen.")]
    private static partial void LogPolled(ILogger logger, string providerName, int receivedCount, int emittedCount, int suppressedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider {ProviderName} failed this poll and produced nothing; it will retry in {PollInterval}. Other sources are unaffected.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception, string providerName, TimeSpan pollInterval);
}
