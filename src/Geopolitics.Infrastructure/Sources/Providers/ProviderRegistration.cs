using System.Net;
using System.Net.Http.Headers;
using Geopolitics.Application.Abstractions;
using Geopolitics.Infrastructure.Sources.Briefs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Registers the live OSINT adapters and the HTTP behaviour they share.
/// </summary>
public static class ProviderRegistration
{
    /// <summary>
    /// Descriptive identifier sent with every outbound request. Public feeds legitimately block
    /// unidentified clients, and naming the project is the courteous and practical thing to do.
    /// </summary>
    private const string UserAgent = "GeoConflux/1.0 (+https://github.com/LimitlessLtd/GeoConflux)";

    /// <summary>
    /// Ceiling on a single response body. A provider that answers a routine poll with hundreds of
    /// megabytes is malfunctioning, and buffering it would turn their fault into this process running
    /// out of memory.
    /// </summary>
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Adds the RSS, FIRMS, ACLED, and UCDP adapters as ingestion sources.
    /// <para>
    /// All of them are registered unconditionally and each decides for itself whether to poll, so
    /// enabling a provider is a configuration change rather than a different set of services having
    /// been composed at startup. Every one of them is dormant under the configuration shipped in this
    /// repository.
    /// </para>
    /// </summary>
    public static IServiceCollection AddOsintProviders(this IServiceCollection services)
    {
        services.AddOptions<ProviderOptions>()
            .BindConfiguration(ProviderOptions.SectionName)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ProviderOptions>, ProviderOptionsValidator>();
        services.AddSingleton<ProviderSecretRedactor>();

        // Registered explicitly because AddLogger<T> resolves the type rather than constructing it,
        // and an unresolvable logger fails when the client is created — which a polling adapter would
        // absorb as "this provider is having a bad day" and retry forever.
        services.AddTransient<RedactingHttpClientLogger>();

        // No base address: each feed is configured as an absolute URL, because the whole point of the
        // RSS adapter is that it is not bound to one publisher's host.
        services.AddResilientProviderClient(RssEventSource.HttpClientName, (_, client) =>
            client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8"));

        services.AddResilientProviderClient(NasaFirmsEventSource.HttpClientName, (options, client) =>
        {
            client.BaseAddress = new Uri(options.NasaFirms.BaseAddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
        });

        services.AddResilientProviderClient(AcledEventSource.HttpClientName, (options, client) =>
        {
            client.BaseAddress = new Uri(options.Acled.BaseAddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddResilientProviderClient(UcdpEventSource.HttpClientName, (options, client) =>
        {
            client.BaseAddress = new Uri(options.Ucdp.BaseAddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // A singleton, because the point of it is to hold one token across polls. Registered as its
        // own service rather than constructed by the adapter so that its lifetime is the container's
        // and its HTTP client comes from the factory, like every other outbound call here.
        services.AddSingleton<AcledTokenProvider>();

        // Shared by the two dataset adapters so that a backfill survives a restart. A singleton
        // holding a scope factory rather than a DbContext: the adapters that use it are singletons,
        // and a context on one of those is the classic way this goes wrong.
        services.AddSingleton<IIngestionCheckpointStore, IngestionCheckpointStore>();

        services.AddSingleton<IEventSource, RssEventSource>();
        services.AddSingleton<IEventSource, NasaFirmsEventSource>();
        services.AddSingleton<IEventSource, AcledEventSource>();
        services.AddSingleton<IEventSource, UcdpEventSource>();

        // Registered here for proximity rather than because it belongs to this family: it opens no
        // connection, holds no credential, and shares none of the HTTP behaviour configured above.
        // What it does share is being an IEventSource, which is the only thing downstream knows.
        services.AddOptions<AgentBriefOptions>()
            .BindConfiguration(AgentBriefOptions.SectionName)
            .ValidateOnStart();

        services.AddSingleton<AgentBriefEventSource>();
        services.AddSingleton<IEventSource>(provider => provider.GetRequiredService<AgentBriefEventSource>());

        return services;
    }

    /// <summary>
    /// Configures one named client with the resilience every external integration in this system
    /// gets.
    /// <para>
    /// The standard handler is used rather than a hand-rolled policy stack because its defaults
    /// already encode the decision that matters most: transient statuses, HTTP 408, and HTTP 429 are
    /// retried, while a 4xx that means "your request is wrong" is not, so a bad credential fails once
    /// instead of four times. What is set below is only what is specific to polling a public feed on
    /// a schedule.
    /// </para>
    /// </summary>
    private static void AddResilientProviderClient(
        this IServiceCollection services,
        string name,
        Action<ProviderOptions, HttpClient> configureClient)
    {
        services.AddHttpClient(name, (provider, client) =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.MaxResponseContentBufferSize = MaxResponseBytes;

            // Left unbounded here on purpose: timeouts are the resilience pipeline's job, and an
            // HttpClient.Timeout shorter than the pipeline's would cancel a request mid-retry and
            // report it as a client fault rather than as the provider being slow.
            client.Timeout = Timeout.InfiniteTimeSpan;

            configureClient(provider.GetRequiredService<IOptions<ProviderOptions>>().Value, client);
        })

        // Every connection these adapters open is screened against the resolved address, which is
        // what makes a redirect into a private network fail rather than succeed quietly. The redirect
        // cap is lowered from the framework default of 50 at the same time: a feed that needs more
        // than a couple of hops is misconfigured, and a long chain is a way to burn a poll slot.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = PublicInternetConnector.ConnectAsync,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
        })

        // The runtime's own HTTP logger is removed rather than left alongside this one. It writes the
        // request path verbatim, and the FIRMS map key is a path segment, so leaving it registered
        // would keep publishing the credential no matter what this logger does.
        .RemoveAllLoggers()
        .AddLogger<RedactingHttpClientLogger>()
        .AddStandardResilienceHandler(options =>
        {
            // One attempt. Generous for a feed, short enough that a hung connection does not occupy
            // the poll slot until the next cycle.
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);

            // The whole operation including retries and their backoff.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);

            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.Delay = TimeSpan.FromSeconds(2);

            // Jitter matters here specifically because several feeds are polled on the same timer.
            // Without it, a provider that rate-limits one request rate-limits the retries of all of
            // them in lockstep.
            options.Retry.UseJitter = true;

            // A provider that states how long to wait knows better than any backoff curve. Honouring
            // Retry-After is what separates cooperating with a rate limit from hammering through it.
            options.Retry.ShouldRetryAfterHeader = true;

            // The breaker is shared across every request this client makes, which for the RSS adapter
            // means several unrelated hosts. That is why the throughput floor is set above the number
            // of requests a normal poll produces: a routine cycle can never open the circuit, and what
            // can is a burst of failures inside one minute, which is the runaway worth stopping.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.MinimumThroughput = 8;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);
        });
    }
}
