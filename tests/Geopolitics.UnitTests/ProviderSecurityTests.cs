using System.Net;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Infrastructure.Sources.Providers;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.UnitTests;

/// <summary>
/// The security properties of the outbound half of the system: what a poll may reach, and what it
/// may write down about itself.
/// </summary>
public sealed class ProviderSecurityTests
{
    private const string FirmsKey = "firms-map-key-ab12cd34";
    private const string AcledKey = "acled-access-key-zz99";
    private const string AcledEmail = "analyst@example.org";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    [Fact]
    public async Task TheFirmsMapKeyNeverReachesTheLogs()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("firms-viirs.csv"), mediaType: "text/csv"));
        var logs = new RecordingLoggerProvider();

        using var provider = Build(handler, NasaFirmsEventSource.HttpClientName, logs, options =>
        {
            options.Mode = ProviderMode.Live;
            options.NasaFirms.Enabled = true;
            options.NasaFirms.ApiKey = FirmsKey;
            options.NasaFirms.PollInterval = TimeSpan.FromMilliseconds(1);
        });

        await DrainAsync(Source<NasaFirmsEventSource>(provider), stop.Token);

        // The key really was sent: this asserts that it was redacted on the way to the log, not that
        // the request never happened.
        Assert.Contains(handler.Requests, request => request.Contains(FirmsKey, StringComparison.Ordinal));
        Assert.DoesNotContain(FirmsKey, logs.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAcledKeyAndRegisteredEmailNeverReachTheLogs()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, ScriptedHttpHandler.Respond(Fixture("acled-response.json"), mediaType: "application/json"));
        var logs = new RecordingLoggerProvider();

        using var provider = Build(handler, AcledEventSource.HttpClientName, logs, options =>
        {
            options.Mode = ProviderMode.Live;
            options.Acled.Enabled = true;
            options.Acled.ApiKey = AcledKey;
            options.Acled.Email = AcledEmail;
            options.Acled.PollInterval = TimeSpan.FromMilliseconds(1);
        });

        await DrainAsync(Source<AcledEventSource>(provider), stop.Token);

        Assert.Contains(handler.Requests, request => request.Contains(AcledKey, StringComparison.Ordinal));
        Assert.DoesNotContain(AcledKey, logs.Transcript, StringComparison.Ordinal);

        // The registered email is a personal identifier as well as half a credential, so it is held
        // to the same standard.
        Assert.DoesNotContain(AcledEmail, logs.Transcript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFeedPointedAtLoopbackIsRefusedByTheRealHttpStack()
    {
        // The transport is the production one here. The tests above prove the policy decides
        // correctly; this proves the policy is actually consulted, which is the part that silently
        // stops being true when a handler registration is reordered.
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Application.Pipeline.PipelineDiagnostics>();
        services.AddOsintProviders();
        services.Configure<ProviderOptions>(options =>
        {
            options.Mode = ProviderMode.Live;
            options.Rss.Enabled = true;
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "loopback", Url = "http://127.0.0.1:9/feed.xml" });
        });
        services.Configure<HttpStandardResilienceOptions>(
            $"{RssEventSource.HttpClientName}-standard",
            options => options.Retry.Delay = TimeSpan.Zero);

        using var provider = services.BuildServiceProvider();

        // One poll, not the continuous loop, so the test ends on its own. The timeout is a backstop
        // against a hang rather than part of what is being asserted.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var batch = await Source<RssEventSource>(provider).ReadBatchAsync(timeout.Token);

        Assert.Empty(batch);
        Assert.Contains("Refusing to connect", logs.Transcript, StringComparison.Ordinal);
    }

    [Theory]

    // Loopback, in both the form a redirect would use and the form that tries to slip past a naive
    // check by writing an IPv4 address as IPv6.
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]

    // The cloud metadata service. This is the single most valuable target an SSRF redirect has, and
    // on most hosted infrastructure it answers without any credential at all.
    [InlineData("169.254.169.254")]

    // RFC 1918 and carrier-grade NAT space: someone else's machine on the same private network.
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.4")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]

    // IPv6 private and link-local space.
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void AnOutboundPollRefusesAddressesThatAreNotOnThePublicInternet(string address)
    {
        Assert.False(OutboundAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("151.101.1.140")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.167.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("2606:4700:4700::1111")]
    public void AnOutboundPollStillReachesOrdinaryPublicAddresses(string address)
    {
        // The boundaries are included deliberately: a policy that blocked 172.32.0.0 or 100.63.0.0
        // would be quietly refusing real feeds, which is a worse failure than it looks because the
        // adapter reports it as a bad day upstream.
        Assert.True(OutboundAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://feeds.invalid/news.xml")]
    [InlineData("/relative/news.xml")]
    [InlineData("not a url")]
    public void AFeedUrlThatIsNotAnAbsoluteHttpUrlStopsStartup(string url)
    {
        using var provider = BuildOptionsOnly(options =>
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "bad", Url = url }));

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ProviderOptions>>().Value);

        Assert.Contains("bad", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A poll interval is what stands between this process and someone else's rate limiter, so a
    /// value that cannot be waited on has to stop the deployment rather than reach the loop.
    /// <para>
    /// Both cases below were checked against the runtime rather than assumed. Zero completes the
    /// delay immediately, turning the poll loop into a hot loop that requests as fast as the network
    /// allows — a self-inflicted flood of a third party's API. A negative value throws
    /// <see cref="ArgumentOutOfRangeException"/> from inside the iterator, where only cancellation is
    /// caught, so the source dies for the lifetime of the process and reports nothing but a single
    /// log line.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APollIntervalThatCannotBeWaitedOnStopsStartup(int seconds)
    {
        using var provider = BuildOptionsOnly(options =>
        {
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "good", Url = "https://feeds.invalid/news.xml" });
            options.Rss.PollInterval = TimeSpan.FromSeconds(seconds);
        });

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ProviderOptions>>().Value);

        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The companion bound. A batch size of zero is not a smaller poll, it is a source that fetches
    /// from the network on every cycle and discards the answer, which looks identical to a dead feed
    /// from the dashboard.
    /// </summary>
    [Fact]
    public void ABatchSizeOfZeroStopsStartup()
    {
        using var provider = BuildOptionsOnly(options =>
        {
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "good", Url = "https://feeds.invalid/news.xml" });
            options.Rss.MaxItemsPerPoll = 0;
        });

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ProviderOptions>>().Value);

        Assert.Contains("MaxItemsPerPoll", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedFeedUrlPassesValidation()
    {
        using var provider = BuildOptionsOnly(options =>
            options.Rss.Feeds.Add(new RssFeedOptions { Name = "good", Url = "https://feeds.invalid/news.xml" }));

        var options = provider.GetRequiredService<IOptions<ProviderOptions>>().Value;

        Assert.Single(options.Rss.Feeds);
    }

    private static ServiceProvider BuildOptionsOnly(Action<ProviderOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Application.Pipeline.PipelineDiagnostics>();
        services.AddOsintProviders();
        services.Configure(configure);
        return services.BuildServiceProvider();
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
        }

        return envelopes;
    }

    private static ServiceProvider Build(
        HttpMessageHandler handler,
        string clientName,
        RecordingLoggerProvider logs,
        Action<ProviderOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // Trace, so nothing is excluded by a level filter. A credential that only appears at Debug
        // is still a credential in a log file the moment someone raises the level to investigate.
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Application.Pipeline.PipelineDiagnostics>();
        services.AddOsintProviders();
        services.Configure(configure);

        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        services.Configure<HttpStandardResilienceOptions>(
            $"{clientName}-standard",
            options => options.Retry.Delay = TimeSpan.Zero);

        return services.BuildServiceProvider();
    }
}
