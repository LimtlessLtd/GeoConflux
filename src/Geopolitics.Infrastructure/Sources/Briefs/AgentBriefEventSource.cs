using System.Runtime.CompilerServices;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Briefs;

/// <summary>
/// Emits observations from recorded collection bundles.
/// <para>
/// The second intake shape. Every other adapter in this project <em>pulls</em>: a timer fires, an
/// HTTP call goes out, a parser runs. This one reads files an OSINT agent left behind, which is what
/// makes it the only source that reaches real reporting with no credential, no outbound connection,
/// and the same result every time it runs — the three properties that let the published dashboard
/// show cited material rather than a recorded demo.
/// </para>
/// <para>
/// It is not a provider and does not inherit <c>PollingEventSource</c>. Nothing here can be rate
/// limited, redirected, or time out, so the resilience that class exists to supply would be
/// answering questions this source never asks.
/// </para>
/// </summary>
public sealed partial class AgentBriefEventSource : IEventSource, IBatchEventSource
{
    private readonly AgentBriefOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentBriefEventSource> logger;
    private readonly string directory;

    public AgentBriefEventSource(
        IOptions<AgentBriefOptions> options,
        TimeProvider timeProvider,
        ILogger<AgentBriefEventSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;

        // Resolved against the deployed binaries rather than the working directory or the content
        // root. Bundles are copied into the build output, so they travel with the application into a
        // container the same way the recorded replay stream does, and the source keeps no dependency
        // on the hosting environment — which is what lets it be composed in a bare container.
        directory = Path.IsPathRooted(this.options.Directory)
            ? this.options.Directory
            : Path.Combine(AppContext.BaseDirectory, this.options.Directory);
    }

    public string Name => "collected";

    public async IAsyncEnumerable<ObservationEnvelope> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            LogDisabled(logger);
            yield break;
        }

        // Suppressed across rescans so a bundle that stays on disk is not re-emitted every cycle.
        // The pipeline would deduplicate it anyway on the source identifier, but making the queue
        // carry the same items every half hour to be thrown away is work nobody asked for.
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var envelope in ReadBundles(cancellationToken))
            {
                if (envelope.SourceIdentifier is { } identifier && !emitted.Add(identifier))
                {
                    continue;
                }

                yield return envelope;
            }

            await Task.Delay(options.RescanInterval, timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Reads every bundle once. Used by the snapshot export, which has to finish and write a file
    /// rather than follow a stream that never ends.
    /// </summary>
    public Task<IReadOnlyList<ObservationEnvelope>> ReadBatchAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            LogDisabled(logger);
            return Task.FromResult<IReadOnlyList<ObservationEnvelope>>([]);
        }

        return Task.FromResult<IReadOnlyList<ObservationEnvelope>>([.. ReadBundles(cancellationToken)]);
    }

    private List<ObservationEnvelope> ReadBundles(CancellationToken cancellationToken)
    {
        var envelopes = new List<ObservationEnvelope>();

        if (!Directory.Exists(directory))
        {
            // Not an error. A deployment that has no bundles yet is a deployment with nothing
            // collected, which is a different and quieter thing from a deployment that is broken.
            LogNoDirectory(logger, directory);
            return envelopes;
        }

        var now = timeProvider.GetUtcNow();
        var limits = options.ToLimits();
        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CollectionBundleReadResult result;

            try
            {
                result = CollectionBundleParser.Parse(File.ReadAllText(file), now, limits);
            }
            catch (FormatException exception)
            {
                // One bad bundle costs its own items and nothing else, exactly as one bad feed costs
                // its own poll. The reason is logged because a bundle that silently produced nothing
                // is the failure hardest to notice from the dashboard.
                LogBundleRejected(logger, Path.GetFileName(file), exception.Message);
                continue;
            }
            catch (IOException exception)
            {
                LogBundleUnreadable(logger, Path.GetFileName(file), exception.Message);
                continue;
            }

            var age = now - result.Bundle.CollectedAt;

            if (age > options.MaxBundleAge)
            {
                LogBundleExpired(logger, result.Bundle.BundleId, age.TotalDays, options.MaxBundleAge.TotalDays);
                continue;
            }

            foreach (var rejection in result.RejectedItems)
            {
                LogItemRejected(logger, result.Bundle.BundleId, rejection);
            }

            foreach (var item in result.Bundle.Items)
            {
                envelopes.Add(ToEnvelope(item, result.Bundle));
            }

            LogBundleRead(logger, result.Bundle.BundleId, result.Bundle.Items.Count, result.RejectedItems.Count);
        }

        return envelopes;
    }

    private static ObservationEnvelope ToEnvelope(CollectedItem item, CollectionBundle bundle) => new()
    {
        // Namespaced so collected material is distinguishable from adapter output at a glance, and
        // so a post carries its channel where a document carries its publisher. A reader must never
        // have to guess which of the two they are looking at.
        SourceName = $"collected:{item.Attribution}",

        Kind = item.Kind == CollectedItemKind.UserGenerated ? ObservationKind.Manual : ObservationKind.News,

        // Title and quotation together, because the gazetteer reads this text and a headline often
        // names the place the excerpt only alludes to.
        Content = string.IsNullOrEmpty(item.Title) ? item.Excerpt : $"{item.Title}\n\n{item.Excerpt}",
        Title = item.Title,

        // The canonical URL is the identity. Re-reading the same bundle, or reading two bundles that
        // both found the same article, is suppressed by Layer 1 of the existing deduplication rather
        // than by anything this source has to remember.
        SourceIdentifier = item.Url.AbsoluteUri,

        OccurredAt = item.PublishedAt,

        // A name only. The gazetteer resolves it or the observation stays unplaced, which is the same
        // path a manual submission takes and the reason a collector cannot put a pin anywhere.
        DeclaredLocationName = item.PlaceNames.Count > 0 ? item.PlaceNames[0] : null,

        Provenance = ObservationProvenance.Collected,
        CollectedAt = bundle.CollectedAt,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Collected-bundle source is disabled by configuration.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "No collection bundle directory at {Directory}; nothing has been collected yet.")]
    private static partial void LogNoDirectory(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collection bundle '{File}' was rejected: {Reason}")]
    private static partial void LogBundleRejected(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collection bundle '{File}' could not be read: {Reason}")]
    private static partial void LogBundleUnreadable(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collection bundle '{BundleId}' is {AgeDays:F1} days old, past the {LimitDays:F1} day limit, and was skipped.")]
    private static partial void LogBundleExpired(ILogger logger, string bundleId, double ageDays, double limitDays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collection bundle '{BundleId}' skipped an item: {Reason}")]
    private static partial void LogItemRejected(ILogger logger, string bundleId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Collection bundle '{BundleId}' yielded {Accepted} item(s); {Rejected} were skipped.")]
    private static partial void LogBundleRead(ILogger logger, string bundleId, int accepted, int rejected);
}
