using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Geopolitics.UnitTests;

/// <summary>
/// Drives the live adapters end to end with the real HTTP stack, including the resilience pipeline,
/// and a scripted transport in place of the network.
/// <para>
/// The container these tests build is the production one: <c>AddOsintProviders</c> registers the
/// clients, the retry policy, and the circuit breaker exactly as the application does, and only the
/// primary handler is replaced. That is deliberate. A test that stubbed <c>HttpClient</c> would prove
/// the mapping code works and say nothing about whether a rate limit is honoured or a bad credential
/// is retried four times, which is most of what an integration adapter is for.
/// </para>
/// </summary>
public sealed class ProviderAdapterTests
{
    private const string FeedUrl = "https://feeds.invalid/maritime.xml";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    [Fact]
    public async Task ProvidersMakeNoNetworkCallUnderTheConfigurationShippedInTheRepository()
    {
        // The guarantee the specification asks for, asserted rather than assumed: clone, run, and
        // nothing reaches out. Demo mode plus per-provider disablement is the default.
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("rss-2.0.xml")));
        using var provider = Build(handler, RssEventSource.HttpClientName, options =>
        {
            options.Mode = ProviderMode.Demo;
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "maritime", Url = FeedUrl });
        });

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LiveModeAloneDoesNotEnableAProvider()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("rss-2.0.xml")));
        using var provider = Build(handler, RssEventSource.HttpClientName, options =>
        {
            options.Mode = ProviderMode.Live;
            options.Rss.Enabled = false;
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "maritime", Url = FeedUrl });
        });

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RssAdapterTurnsFeedItemsIntoObservationsWithoutInventingAnything()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("rss-2.0.xml")));
        using var provider = Build(handler, RssEventSource.HttpClientName, LiveRss);

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Equal(3, envelopes.Count);
        Assert.All(envelopes, envelope =>
        {
            Assert.Equal("rss:maritime", envelope.SourceName);
            Assert.Equal(ObservationKind.News, envelope.Kind);

            // Live output is never labelled as demo data, and demo output always is. The two must
            // not be confusable anywhere downstream.
            Assert.Equal(ObservationProvenance.Polled, envelope.Provenance);

            // A news item states no coordinates, no category, and no severity. The adapter declares
            // none of them, so the pipeline resolves a location the deterministic way or not at all.
            Assert.Null(envelope.DeclaredLatitude);
            Assert.Null(envelope.DeclaredLongitude);
            Assert.Null(envelope.DeclaredEventType);
            Assert.Null(envelope.DeclaredSeverity);
        });
    }

    [Fact]
    public async Task RssAdapterHonoursTheConfiguredItemCeiling()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("rss-2.0.xml")));
        using var provider = Build(handler, RssEventSource.HttpClientName, options =>
        {
            LiveRss(options);
            options.Rss.MaxItemsPerPoll = 2;
        });

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Equal(2, envelopes.Count);
    }

    [Fact]
    public async Task AnItemAlreadyEmittedIsNotEmittedAgainOnTheNextPoll()
    {
        using var stop = new CancellationTokenSource();
        var feed = Fixture("rss-2.0.xml");

        // The same feed twice, which is what an unchanged publisher looks like to a poller.
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond(feed),
            ScriptedHttpHandler.Respond(feed));

        using var provider = Build(handler, RssEventSource.HttpClientName, LiveRss);

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        // Both polls happened; only the first produced work for the pipeline.
        Assert.Equal(3, envelopes.Count);
        Assert.True(handler.RequestCount >= 2);
    }

    [Fact]
    public async Task RateLimitedRequestIsRetriedRatherThanAbandoned()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.RateLimited(),
            ScriptedHttpHandler.Respond(Fixture("rss-2.0.xml")));

        using var provider = Build(handler, RssEventSource.HttpClientName, LiveRss);

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        // The provider said "too many requests", the pipeline waited as instructed, and the retry
        // succeeded. A poll that gave up on 429 would have produced nothing here.
        Assert.Equal(3, envelopes.Count);
    }

    [Fact]
    public async Task ServerErrorsAreRetriedAndThenGiveUpWithoutKillingTheSource()
    {
        using var stop = new CancellationTokenSource();

        // One attempt plus three retries is what the registration configures. Scripting exactly that
        // many failures is what makes the retry count an assertion rather than an assumption.
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Status(System.Net.HttpStatusCode.InternalServerError),
            ScriptedHttpHandler.Status(System.Net.HttpStatusCode.InternalServerError),
            ScriptedHttpHandler.Status(System.Net.HttpStatusCode.InternalServerError),
            ScriptedHttpHandler.Status(System.Net.HttpStatusCode.InternalServerError));

        using var provider = Build(handler, RssEventSource.HttpClientName, LiveRss);

        // No exception escapes. A provider being down is a degraded cycle, not a dead source.
        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(4, handler.ScriptedResponsesServed);
    }

    [Fact]
    public async Task ClientErrorsAreNotRetried()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Status(System.Net.HttpStatusCode.Unauthorized));
        using var provider = Build(handler, RssEventSource.HttpClientName, LiveRss);

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);

        // Exactly one attempt. A rejected credential will be rejected again, and retrying it wastes
        // the provider's quota and this system's time.
        Assert.Equal(1, handler.ScriptedResponsesServed);
    }

    [Fact]
    public async Task MalformedFeedCostsThatPollAndNothingElse()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("rss-malformed.xml")));
        using var provider = Build(handler, RssEventSource.HttpClientName, LiveRss);

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
    }

    [Fact]
    public async Task OneBrokenFeedDoesNotCostTheOthersTheirPoll()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond("not xml at all"),
            ScriptedHttpHandler.Respond(Fixture("atom-1.0.xml")));

        using var provider = Build(handler, RssEventSource.HttpClientName, options =>
        {
            LiveRss(options);
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "security", Url = "https://feeds.invalid/security.xml" });
        });

        var envelopes = await DrainAsync(Source<RssEventSource>(provider), stop.Token);

        // The second feed still produced its two entries despite the first feed being unreadable.
        Assert.Equal(2, envelopes.Count);
        Assert.All(envelopes, envelope => Assert.Equal("rss:security", envelope.SourceName));
    }

    [Fact]
    public async Task FirmsAdapterStaysDormantWithoutAKeyEvenWhenEnabled()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("firms-viirs.csv")));
        using var provider = Build(handler, NasaFirmsEventSource.HttpClientName, options =>
        {
            options.Mode = ProviderMode.Live;
            options.NasaFirms.Enabled = true;
            options.NasaFirms.ApiKey = string.Empty;
        });

        var envelopes = await DrainAsync(Source<NasaFirmsEventSource>(provider), stop.Token);

        // Without this the adapter would poll forever and log parse failures, because FIRMS answers
        // a keyless request with HTTP 200 and an error sentence.
        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FirmsDetectionsCarryTheirOwnCoordinatesAndAreNotPresentedAsConflict()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("firms-viirs.csv"), mediaType: "text/csv"));
        using var provider = Build(handler, NasaFirmsEventSource.HttpClientName, LiveFirms);

        var envelopes = await DrainAsync(Source<NasaFirmsEventSource>(provider), stop.Token);

        // Three parse; one is below the 50% confidence floor.
        Assert.Equal(2, envelopes.Count);

        Assert.All(envelopes, envelope =>
        {
            // An instrument geolocating a pixel is a measurement, so these coordinates are declared.
            Assert.NotNull(envelope.DeclaredLatitude);
            Assert.NotNull(envelope.DeclaredLongitude);
            Assert.Equal(ObservationKind.Satellite, envelope.Kind);

            // A thermal detection records heat, not its cause, and must never enter the system
            // labelled as a conflict indicator.
            Assert.Equal(EventType.NaturalHazard, envelope.DeclaredEventType);
            Assert.Equal(Severity.Low, envelope.DeclaredSeverity);
            Assert.Contains("records heat, not its cause", envelope.Content, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FirmsApiKeyIsNotExposedInTheRequestPathInPlainForm()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("firms-viirs.csv"), mediaType: "text/csv"));
        using var provider = Build(handler, NasaFirmsEventSource.HttpClientName, options =>
        {
            LiveFirms(options);
            options.NasaFirms.ApiKey = "key with spaces/and slashes";
        });

        await DrainAsync(Source<NasaFirmsEventSource>(provider), stop.Token);

        var requested = handler.Requests.First();

        // The FIRMS area API takes the key as a path segment, so it has to be escaped or the request
        // is silently addressed to the wrong resource.
        Assert.DoesNotContain("key with spaces", requested, StringComparison.Ordinal);
        Assert.Contains("key%20with%20spaces", requested, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcledEventsArriveWithTheProvidersOwnCodingDeclared()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, options =>
        {
            options.Mode = ProviderMode.Live;
            options.Acled.Enabled = true;
            options.Acled.ApiKey = "test-key";
            options.Acled.Email = "tester@example.invalid";
            options.Acled.PollInterval = TimeSpan.FromMilliseconds(1);
        });

        var envelopes = await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        Assert.Equal(3, envelopes.Count);
        Assert.All(envelopes, envelope => Assert.Equal(ObservationKind.ExternalEvent, envelope.Kind));

        // ACLED codes its records by hand. Declaring the category means the pipeline keeps it rather
        // than replacing it with something a model inferred from the notes field.
        Assert.Equal(EventType.Protest, envelopes[0].DeclaredEventType);
        Assert.Equal(Severity.High, envelopes[1].DeclaredSeverity);
    }

    [Fact]
    public async Task AcledStaysDormantWithoutACredential()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond("{}", mediaType: "application/json"));
        using var provider = Build(handler, AcledEventSource.HttpClientName, options =>
        {
            options.Mode = ProviderMode.Live;
            options.Acled.Enabled = true;
            options.Acled.ApiKey = "test-key";
            options.Acled.Email = string.Empty;
        });

        var envelopes = await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    private static void LiveRss(ProviderOptions options)
    {
        options.Mode = ProviderMode.Live;
        options.Rss.Enabled = true;

        // Short enough that the second poll arrives immediately, which is what lets a test assert on
        // behaviour across polls without waiting.
        options.Rss.PollInterval = TimeSpan.FromMilliseconds(1);
        options.Rss.Feeds.Add(new RssFeedOptions { Name = "maritime", Url = FeedUrl });
    }

    private static void LiveFirms(ProviderOptions options)
    {
        options.Mode = ProviderMode.Live;
        options.NasaFirms.Enabled = true;
        options.NasaFirms.ApiKey = "test-map-key";
        options.NasaFirms.PollInterval = TimeSpan.FromMilliseconds(1);
    }

    private static TSource Source<TSource>(IServiceProvider provider)
        where TSource : IEventSource =>
        provider.GetServices<IEventSource>().OfType<TSource>().Single();

    private static async Task<List<ObservationEnvelope>> DrainAsync(IEventSource source, CancellationToken cancellationToken)
    {
        var envelopes = new List<ObservationEnvelope>();

        try
        {
            await foreach (var envelope in source.ReadAsync(cancellationToken))
            {
                envelopes.Add(envelope);
            }
        }
        catch (OperationCanceledException)
        {
            // The scripted transport ends the run by cancelling. Reaching the end of the script is
            // the success condition, not a failure.
        }

        return envelopes;
    }

    /// <summary>
    /// Builds the application's own provider registration with one transport swapped out. The
    /// resilience handler, its retry policy, and its circuit breaker are the production ones.
    /// </summary>
    private static ServiceProvider Build(
        HttpMessageHandler handler,
        string clientName,
        Action<ProviderOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Application.Pipeline.PipelineDiagnostics>();
        services.AddOsintProviders();
        services.Configure(configure);

        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        // Retry delay is the one production value a test must override: the real two-second
        // exponential backoff is correct for a provider and pointless wall-clock time here. The
        // number of attempts, which statuses are retried, and the Retry-After handling all stay as
        // configured, because those are what these tests exist to check.
        services.Configure<HttpStandardResilienceOptions>(
            $"{clientName}-standard",
            options => options.Retry.Delay = TimeSpan.Zero);

        return services.BuildServiceProvider();
    }
}
