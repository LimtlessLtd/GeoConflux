using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Polls configured RSS or Atom feeds and turns their items into observations.
/// <para>
/// No publisher is named anywhere in this class: feeds are entirely a matter of configuration, so
/// adding, replacing, or removing a news source is a deployment change rather than a code change.
/// That is the point of the adapter, and it is also what keeps the specification's rule that no one
/// publisher becomes an architectural dependency.
/// </para>
/// <para>
/// A news item carries no coordinates. The adapter deliberately does not guess any: it emits the text
/// and lets enrichment propose a place name which the deterministic resolver then accepts or rejects,
/// which is the same path a manual submission takes.
/// </para>
/// </summary>
public sealed partial class RssEventSource(
    IHttpClientFactory httpClientFactory,
    IOptions<ProviderOptions> options,
    PipelineDiagnostics diagnostics,
    ISourceLivenessRecorder liveness,
    TimeProvider timeProvider,
    ILogger<RssEventSource> logger) : PollingEventSource(diagnostics, liveness, timeProvider, logger)
{
    public const string HttpClientName = "osint.rss";

    private readonly ProviderOptions options = options.Value;

    public override string Name => "rss";

    protected override bool IsEnabled => options.IsLive(options.Rss) && options.Rss.Feeds.Count > 0;

    protected override TimeSpan PollInterval => options.Rss.PollInterval;

    protected override async Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken)
    {
        var envelopes = new List<ObservationEnvelope>();
        var client = httpClientFactory.CreateClient(HttpClientName);

        foreach (var feed in options.Rss.Feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.Url) || string.IsNullOrWhiteSpace(feed.Name))
            {
                LogFeedMisconfigured(Logger, feed.Name);
                continue;
            }

            // One feed's failure must not cost the others their poll, so each is contained here
            // rather than allowed to abort the batch.
            try
            {
                envelopes.AddRange(await ReadFeedAsync(client, feed, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFeedFailed(Logger, exception, feed.Name);
            }
        }

        return envelopes;
    }

    private async Task<IReadOnlyList<ObservationEnvelope>> ReadFeedAsync(
        HttpClient client,
        RssFeedOptions feed,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(feed.Url, cancellationToken);

        // Thrown, not returned: the caller above records it against this feed, and the resilience
        // handler has already exhausted its retries by the time a failure status reaches here.
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var entries = SyndicationFeedParser.Parse(body);
        var sourceName = $"rss:{feed.Name.Trim()}";

        return [.. entries
            .Take(options.Rss.MaxItemsPerPoll)
            .Select(entry => new ObservationEnvelope
            {
                SourceName = sourceName,
                Kind = ObservationKind.News,
                Content = entry.Summary,
                SourceIdentifier = entry.Identifier,
                Title = entry.Title,
                OccurredAt = entry.PublishedAt,

                // The one thing a feed does declare about itself. Everything else — category,
                // severity, position — it states nothing about, and inventing any of them here would
                // put a guess where the pipeline expects a fact. Language is different: the channel
                // says what it publishes in, and that beats inferring it from the script, which
                // cannot tell French from English because both are written in the same alphabet.
                DeclaredLanguage = entry.Language,

                Provenance = ObservationProvenance.Polled,
            })];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "RSS feed '{FeedName}' is missing a name or URL and was skipped.")]
    private static partial void LogFeedMisconfigured(ILogger logger, string feedName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RSS feed '{FeedName}' could not be read this poll; other feeds were unaffected.")]
    private static partial void LogFeedFailed(ILogger logger, Exception exception, string feedName);
}
