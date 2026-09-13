using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Application.Coverage;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Pipeline;
using Geopolitics.Application.Spatial;
using Geopolitics.Infrastructure.Ai;
using Geopolitics.Infrastructure.Hosting;
using Geopolitics.Infrastructure.Location;
using Geopolitics.Infrastructure.Ml;
using Geopolitics.Infrastructure.Persistence;
using Geopolitics.Infrastructure.Queue;
using Geopolitics.Infrastructure.Realtime;
using Geopolitics.Infrastructure.Sources;
using Geopolitics.Infrastructure.Sources.Briefs;
using Geopolitics.Infrastructure.Sources.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence and read models. This is everything a host needs to serve the API; it
    /// deliberately does not start any background work.
    /// </summary>
    public static IServiceCollection AddGeopoliticsInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);

        // The connection string is read when the context is resolved rather than captured here.
        // Reading it eagerly would freeze whatever value existed at registration time and silently
        // ignore configuration sources added later in the host build.
        services.AddDbContext<GeopoliticsDbContext>((provider, options) =>
            options.UseSqlite(ResolveConnectionString(provider.GetRequiredService<IConfiguration>())));
        services.AddScoped<IIncidentRepository, EfIncidentRepository>();
        services.AddScoped<IObservationRepository, EfObservationRepository>();
        services.AddScoped<IAiInferenceRepository, EfAiInferenceRepository>();
        services.AddScoped<IAnalyticsRepository, EfAnalyticsRepository>();
        services.AddScoped<ICoverageRepository, EfCoverageRepository>();
        services.AddScoped<IIncidentQueryService, IncidentQueryService>();
        services.AddScoped<ISpatialQueryService, SpatialQueryService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<ICoverageService, CoverageService>();

        // Singleton because it reports on tables built once at startup and holds no per-request
        // state. It is the one component that answers "how much could this system place at all",
        // which the coverage report needs and the resolver has no reason to expose.
        services.AddSingleton<IPlaceLexicon, GazetteerPlaceLexicon>();

        // Reads the committed bundles for what each run asked and what came back. Singleton because
        // it holds no request state and its input is files on disk.
        services.AddSingleton<ICollectionCoverage, BundleCollectionCoverage>();

        // Reference data, immutable and shared.
        services.AddSingleton<IChokepointCatalogue, MaritimeChokepointCatalogue>();
        services.AddScoped<IObservationQueryService, ObservationQueryService>();
        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// Registers the asynchronous processing pipeline and the background services that drive it.
    /// <para>
    /// Kept separate from <see cref="AddGeopoliticsInfrastructure"/> so a host can serve the API
    /// without also processing the queue, and so tests can drive the processor directly without
    /// background services racing them.
    /// </para>
    /// </summary>
    public static IServiceCollection AddGeopoliticsPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PipelineOptions>()
            .Bind(configuration.GetSection(PipelineOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ReplayOptions>()
            .Bind(configuration.GetSection(ReplayOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<EnrichmentOptions>()
            .Bind(configuration.GetSection(EnrichmentOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<AiProviderOptions>()
            .Bind(configuration.GetSection(AiProviderOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PipelineDiagnostics>();

        // One queue instance serving three roles, so producers, the consumer, and the health check
        // all observe the same channel.
        services.AddSingleton<ChannelObservationBuffer>();
        services.AddSingleton<IObservationQueueWriter>(provider => provider.GetRequiredService<ChannelObservationBuffer>());
        services.AddSingleton<IObservationQueueReader>(provider => provider.GetRequiredService<ChannelObservationBuffer>());
        services.AddSingleton<IObservationQueueMonitor>(provider => provider.GetRequiredService<ChannelObservationBuffer>());

        services.AddSingleton<IEventClassifier, KeywordEventClassifier>();

        services.AddOptions<SeverityModelOptions>()
            .Bind(configuration.GetSection(SeverityModelOptions.SectionName))
            .ValidateOnStart();

        // Singleton because the trained model is the expensive part and is immutable once fitted.
        // Training happens lazily on first prediction, so registering it costs nothing in a host
        // that never processes an observation.
        services.AddSingleton<ISeverityModel, MLNetSeverityModel>();

        // Deterministic and stateless, so one instance serves every worker. Registered against the
        // interface so an embedding-backed measure can replace it without touching the correlator.
        services.AddSingleton<ITextSimilarity, LexicalTextSimilarity>();

        // Singleton by necessity, not convenience: its whole purpose is to be the one thing two
        // concurrent processor scopes contend on.
        services.AddSingleton<CorrelationLock>();

        // Scoped, not singleton: it reads through the repository, which carries the request's
        // DbContext, and a singleton would capture the first one it ever saw.
        services.AddScoped<CorroborationGate>();

        services.AddSingleton<IObservationIngestionService, ObservationIngestionService>();

        // The mock client is registered unconditionally so that switching Ai:Provider back to Mock
        // is a configuration change, and so tests can resolve it without rebuilding the container.
        services.AddSingleton<DeterministicMockChatClient>();
        services.AddSingleton(provider => ChatClientFactory.Create(
            provider.GetRequiredService<IOptions<AiProviderOptions>>().Value,
            provider));
        services.AddSingleton<IEventEnrichmentService, ChatClientEnrichmentService>();

        services.AddScoped<IObservationNormaliser, ObservationNormaliser>();
        services.AddScoped<ILocationResolver, GazetteerLocationResolver>();
        services.AddScoped<IIncidentCorrelator, DeterministicIncidentCorrelator>();
        services.AddScoped<IObservationProcessor, ObservationProcessor>();

        // A host that wants realtime delivery overrides this; without an override the pipeline still
        // runs end to end and simply announces nothing.
        services.TryAddSingleton<IIncidentNotifier, NullIncidentNotifier>();

        // Registered unconditionally; the source itself honours Replay:Enabled when it runs, so the
        // setting stays live rather than being baked into the container at startup.
        services.AddSingleton<IEventSource, ReplayEventSource>();

        // Live adapters, on the same footing as the recorded one. Each stays dormant unless its own
        // configuration turns it on, so this call adds capability without adding any network traffic.
        services.AddOsintProviders();

        services.AddHostedService<EventSourcePumpService>();
        services.AddHostedService<ObservationProcessorService>();

        services.AddHealthChecks()
            .AddCheck<ObservationQueueHealthCheck>("processing-queue", tags: ["pipeline"]);

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Geopolitics");

        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("ConnectionStrings:Geopolitics must be configured.")
            : connectionString;
    }
}
