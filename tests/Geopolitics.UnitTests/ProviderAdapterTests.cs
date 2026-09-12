using System.Net;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

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
            AcledToken(),
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, LiveAcled);

        var envelopes = await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        Assert.Equal(3, envelopes.Count);
        Assert.All(envelopes, envelope => Assert.Equal(ObservationKind.ExternalEvent, envelope.Kind));

        // ACLED codes its records by hand. Declaring the category means the pipeline keeps it rather
        // than replacing it with something a model inferred from the notes field.
        Assert.Equal(EventType.Protest, envelopes[0].DeclaredEventType);
        Assert.Equal(Severity.High, envelopes[1].DeclaredSeverity);

        // The country name ACLED publishes, turned into the alpha-2 code the rest of the system uses.
        // The retired API had an iso3 field, the current one does not, and the previous adapter threw
        // away everything that was not already two characters — so it never produced a code at all.
        Assert.Equal("UA", envelopes[0].DeclaredCountryCode);
        Assert.Equal("YE", envelopes[1].DeclaredCountryCode);
    }

    /// <summary>
    /// The migration to OAuth, asserted at the wire: a token is fetched from the token endpoint and
    /// then presented as a bearer credential on the read. The retired API took a key and an email as
    /// query parameters against a host that no longer resolves, so a request in that shape reaching
    /// the transport would mean the old adapter was still in place.
    /// </summary>
    [Fact]
    public async Task AcledAuthenticatesWithATokenRatherThanAKeyInTheQueryString()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            AcledToken(),
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, LiveAcled);

        await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        var exchanges = handler.Exchanges.ToArray();

        var token = exchanges[0];
        Assert.Equal("POST", token.Method);
        Assert.Equal("https://acleddata.com/oauth/token", token.Url);

        var grant = Assert.IsType<string>(token.Body);
        Assert.Contains("grant_type=password", grant, StringComparison.Ordinal);
        Assert.Contains("client_id=acled", grant, StringComparison.Ordinal);
        Assert.Contains("scope=authenticated", grant, StringComparison.Ordinal);

        var read = exchanges[1];
        Assert.Equal("GET", read.Method);
        Assert.StartsWith("https://acleddata.com/api/acled/read", read.Url, StringComparison.Ordinal);
        Assert.Equal("Bearer fixture-access-token-not-a-credential", read.Authorization);

        // The credential travels in the token request and nowhere else. The retired query shape put
        // it on every single read.
        Assert.DoesNotContain("key=", read.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("email=", read.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token valid for twenty-four hours has to survive between six-hourly polls, or the account
    /// rate limit is spent on authentication rather than on data.
    /// </summary>
    [Fact]
    public async Task AcledReusesItsAccessTokenAcrossPollsRatherThanReAuthenticating()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            AcledToken(),
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"),
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, LiveAcled);

        await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        // Two polls, one token request. Asserted as a count rather than as an ordering, because what
        // this guards against is a token request reappearing before every read.
        Assert.Equal(1, handler.Exchanges.Count(exchange =>
            exchange.Url.Contains("/oauth/token", StringComparison.Ordinal)));
        Assert.True(
            handler.Exchanges.Count(exchange => exchange.Url.Contains("/acled/read", StringComparison.Ordinal)) >= 2,
            "The adapter should have polled at least twice.");
    }

    /// <summary>
    /// A token can stop being honoured before its stated expiry — revoked, or invalidated by a change
    /// on the account — and the cached copy looks perfectly valid from here. Without this recovery it
    /// would be replayed on every poll until the process restarted.
    /// </summary>
    [Fact]
    public async Task AcledReAuthenticatesOnceWhenACachedTokenIsRefused()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            AcledToken(),
            ScriptedHttpHandler.Status(HttpStatusCode.Unauthorized),
            AcledToken(),
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, LiveAcled);

        var envelopes = await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        // Recovered inside the one poll rather than returning nothing and waiting six hours.
        Assert.Equal(3, envelopes.Count);
        Assert.Equal(2, handler.Exchanges.Count(exchange =>
            exchange.Url.Contains("/oauth/token", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A rejected credential has to be reported as a rejected credential. Answering a wrong password
    /// with "zero events" is the failure mode hardest to notice from the dashboard, and it is what
    /// the retired adapter would now do against a hostname that no longer resolves.
    /// </summary>
    [Fact]
    public async Task AcledReportsARejectedCredentialRatherThanReturningNothingQuietly()
    {
        using var stop = new CancellationTokenSource();
        var logs = new RecordingLoggerProvider();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond(
                Fixture("acled-token-rejected.json"),
                HttpStatusCode.BadRequest,
                "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, LiveAcled, logs);

        var envelopes = await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Contains("invalid_grant", logs.Transcript, StringComparison.Ordinal);
        Assert.Contains("The user credentials were incorrect.", logs.Transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// Country filtering is how this adapter is pointed at a theatre. The exact-match companion
    /// parameter is the part worth pinning: ACLED defaults a text filter to LIKE, which would widen a
    /// request for one country into every country whose name contains it.
    /// </summary>
    [Fact]
    public async Task AcledAsksForTheConfiguredCountriesAsExactMatches()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            AcledToken(),
            ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));

        using var provider = Build(handler, AcledEventSource.HttpClientName, options =>
        {
            LiveAcled(options);
            options.Acled.Countries.Add("Ukraine");
            options.Acled.Countries.Add("Yemen");
            options.Acled.Countries.Add("Ethiopia");
        });

        await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        var read = handler.Exchanges.First(exchange =>
            exchange.Url.Contains("/acled/read", StringComparison.Ordinal));

        // The pipe is ACLED's separator for several values of one field, escaped as %7C on the wire.
        Assert.Contains("country=Ukraine%7CYemen%7CEthiopia", read.Url, StringComparison.Ordinal);
        Assert.Contains("country_where=%3D", read.Url, StringComparison.Ordinal);
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
            options.Acled.Username = "tester@example.invalid";
            options.Acled.Password = string.Empty;
        });

        var envelopes = await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>
    /// A deployment still carrying the retired key-and-email configuration has neither a username nor
    /// a password, so it stays dormant instead of authenticating against an endpoint that would
    /// refuse it. Silence is the right answer to a credential shape that no longer exists.
    /// </summary>
    [Fact]
    public async Task AcledStaysDormantUnderTheRetiredKeyAndEmailConfiguration()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond("{}", mediaType: "application/json"));
        using var provider = Build(handler, AcledEventSource.HttpClientName, options =>
        {
            options.Mode = ProviderMode.Live;
            options.Acled.Enabled = true;
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

    private static void LiveAcled(ProviderOptions options)
    {
        options.Mode = ProviderMode.Live;
        options.Acled.Enabled = true;
        options.Acled.Username = "tester@example.invalid";
        options.Acled.Password = "not-a-real-password";
        options.Acled.PollInterval = TimeSpan.FromMilliseconds(1);
    }

    /// <summary>A successful token grant, which every live ACLED poll now begins with.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> AcledToken() =>
        ScriptedHttpHandler.Respond(Fixture("acled-token.json"), mediaType: "application/json");

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
        Action<ProviderOptions> configure) =>
        Build(handler, clientName, configure, logs: null);

    /// <summary>
    /// As above, capturing log output. Separate from the common overload because most of these tests
    /// assert on envelopes and requests, and only the ones about diagnosis need the transcript.
    /// </summary>
    private static ServiceProvider Build(
        HttpMessageHandler handler,
        string clientName,
        Action<ProviderOptions> configure,
        RecordingLoggerProvider? logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs);
            }
        });
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
