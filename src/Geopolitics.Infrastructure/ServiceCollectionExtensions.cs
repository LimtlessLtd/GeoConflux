using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Application.Conflicts;
using Geopolitics.Application.Control;
using Geopolitics.Application.Coverage;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Operations;
using Geopolitics.Application.Pipeline;
using Geopolitics.Application.Spatial;
using Geopolitics.Infrastructure.Ai;
using Geopolitics.Infrastructure.Conflicts;
using Geopolitics.Infrastructure.Hosting;
using Geopolitics.Infrastructure.Location;
using Geopolitics.Infrastructure.Ml;
using Geopolitics.Infrastructure.Persistence;
using Geopolitics.Infrastructure.Queue;
using Geopolitics.Infrastructure.Realtime;
using Geopolitics.Infrastructure.Sources;
using Geopolitics.Infrastructure.Sources.Briefs;
using Geopolitics.Infrastructure.Sources.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        // The connection string is read when the context is resolved rather than captured here.
        // Reading it eagerly would freeze whatever value existed at registration time and silently
        // ignore configuration sources added later in the host build.
        services.AddDbContext<GeopoliticsDbContext>((provider, options) =>
            options.UseSqlite(ResolveConnectionString(
                provider.GetRequiredService<IConfiguration>(),
                provider.GetService<IHostEnvironment>()?.ContentRootPath)));
        services.AddScoped<IIncidentRepository, EfIncidentRepository>();
        services.AddScoped<IObservationRepository, EfObservationRepository>();
        services.AddScoped<IAiInferenceRepository, EfAiInferenceRepository>();
        services.AddScoped<IAnalyticsRepository, EfAnalyticsRepository>();
        services.AddScoped<ICoverageRepository, EfCoverageRepository>();
        services.AddScoped<IConflictActivityRepository, EfConflictActivityRepository>();

        // Assessed control. Scoped like every other read model; the options it depends on are bound
        // here rather than with the pipeline because the assessment is served whether or not this
        // host is ingesting anything.
        services.AddOptions<ControlOptions>().BindConfiguration(ControlOptions.SectionName);
        services.AddScoped<IControlRepository, EfControlRepository>();
        services.AddScoped<IControlAssessmentService, ControlAssessmentService>();

        // What the host holds rather than what it has reached. Scoped like every other repository,
        // and measured on demand: a cached figure would answer questions about a database that has
        // moved on, and the whole point of it is to be current enough to decide retention against.
        services.AddScoped<IOperationsRepository, SqliteOperationsRepository>();
        services.AddScoped<IOperationsService, OperationsService>();

        // The thing that measures and the thing that deletes are separate registrations of separate
        // interfaces on purpose. The report is served to anybody who can reach the dashboard; there
        // is no request that should be able to reach the second.
        services.AddScoped<IRetentionRepository, EfRetentionRepository>();

        // The downtime ledger. Scoped like the other repositories; the service above it holds the
        // rule that turns a gap between polls into a period this host was away.
        services.AddScoped<IContinuityRepository, EfContinuityRepository>();
        services.AddScoped<IContinuityService, ContinuityService>();

        // Singleton because it is the count a hosted service writes and a scoped report reads. It
        // holds what retention has done since this host started, which is deliberately not durable:
        // see the note on the type.
        services.AddSingleton<RetentionLog>();

        // Singleton for the same reason, and it answers a question this sprint was asked to answer
        // with a number rather than a date: what measurement triggers the move to PostGIS. Written
        // by every spatial search, read by the operations report.
        services.AddSingleton<SpatialScaleLog>();

        // Bound here rather than beside the hosted service because the operations report states the
        // policy whether or not any host is running it, and a report that could not name the horizon
        // would be reporting a prunable count against an unstated rule.
        services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.SectionName);
        services.AddScoped<IIncidentQueryService, IncidentQueryService>();
        services.AddScoped<ISpatialQueryService, SpatialQueryService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<ICoverageService, CoverageService>();
        services.AddScoped<IConflictActivityService, ConflictActivityService>();

        // Singleton because it reports on tables built once at startup and holds no per-request
        // state. It is the one component that answers "how much could this system place at all",
        // which the coverage report needs and the resolver has no reason to expose.
        services.AddSingleton<IPlaceLexicon, GazetteerPlaceLexicon>();

        // Reads the committed bundles for what each run asked and what came back. Singleton because
        // it holds no request state and its input is files on disk.
        services.AddSingleton<ICollectionCoverage, BundleCollectionCoverage>();

        // Reference data, immutable and shared.
        services.AddSingleton<IChokepointCatalogue, MaritimeChokepointCatalogue>();

        // The register of conflicts, and the deterministic assignment of reports into it. Singleton
        // because the register is a committed extract resolved once through the gazetteer, and
        // because rebuilding it per request would repeat thirteen thousand place lookups for an
        // answer that cannot have changed.
        services.AddSingleton<IConflictRegister, CodedConflictRegister>();
        services.AddSingleton<IConflictAssigner, ConflictAssigner>();

        // Asked only where the deterministic assignment could not decide, which is why it sits beside
        // the enrichment service rather than inside it: the question depends on where the report was
        // placed, and placement depends on what enrichment already answered.
        services.AddScoped<IConflictClassifier, ChatClientConflictClassifier>();
        services.AddScoped<IConflictNarrator, ChatClientConflictNarrator>();
        services.AddScoped<IConflictNarrativeService, ConflictNarrativeService>();
        services.AddScoped<IObservationQueryService, ObservationQueryService>();
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

        // A plain client, and deliberately so: the screened handler the OSINT adapters use refuses
        // to connect to a private address, which is what a daemon on localhost is. Nothing leaves the
        // machine on this one. The timeout is generous because a cold model load is a first-call cost
        // of a minute or more, and failing that would look like the daemon being down.
        services.AddHttpClient(ChatClientFactory.OllamaClientName, client =>
            client.Timeout = TimeSpan.FromMinutes(5));
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

        // Every source this host can register reads something real. There is deliberately no
        // recorded or synthetic one: a fabricated observation has no way in, rather than being
        // switched off by a setting somebody could switch back on (ADR 040).
        //
        // Each adapter stays dormant unless its own configuration turns it on, so this call adds
        // capability without adding any network traffic.
        services.AddOsintProviders();

        // First, so the lexicon is built before anything can ask it a question. Hosted services start
        // in registration order, and leaving this to the first observation put a second and a half
        // inside a request rather than inside startup.
        services.AddHostedService<LexiconWarmUpService>();

        // Before the pump, and the order is load-bearing. The ledger measures the gap between the
        // newest poll on record and now; the pump's first poll overwrites that record within
        // milliseconds of starting, so a ledger running after it would measure nothing every time.
        services.AddHostedService<DowntimeLedgerService>();

        services.AddHostedService<EventSourcePumpService>();
        services.AddHostedService<ObservationProcessorService>();

        // Backups are registered here rather than beside the DbContext because this is the call a
        // host makes when it is opting into background work at all; AddGeopoliticsInfrastructure
        // deliberately starts none, and tests depend on that. The service is inert until a
        // destination is named, so registering it costs a host that does not want it nothing.
        services.AddOptions<BackupOptions>()
            .Bind(configuration.GetSection(BackupOptions.SectionName))
            .ValidateOnStart();

        services.AddHostedService<DatabaseBackupService>();

        // Inert unless Retention:Enabled, and its first pass is one interval after start-up rather
        // than at start-up: a host restarted while somebody is reading a failure should not delete
        // that failure as its first act.
        services.AddHostedService<RetentionService>();

        services.AddHealthChecks()
            .AddCheck<ObservationQueueHealthCheck>("processing-queue", tags: ["pipeline"]);

        return services;
    }

    /// <summary>
    /// The configured connection string, with a relative database path anchored to the content root.
    /// </summary>
    /// <remarks>
    /// SQLite resolves a relative <c>Data Source</c> against the process working directory, and the
    /// shipped setting is the relative <c>geopolitics.db</c>. That is harmless for <c>dotnet run</c>,
    /// where the working directory is the project, and wrong in the deployment this repository is
    /// now aiming at: a Windows service starts in <c>C:\Windows\System32</c>, so the host would
    /// create an empty database there and report itself healthy while holding none of the history it
    /// was left running to accumulate. Nothing would fail; the data would simply be somewhere else.
    ///
    /// The content root is the right anchor rather than the assembly directory, because it is the
    /// project directory under <c>dotnet run</c> and the binary directory under a service — so a
    /// developer's existing database does not move, and a service's is beside its executable.
    /// </remarks>
    private static string ResolveConnectionString(IConfiguration configuration, string? contentRoot)
    {
        var connectionString = configuration.GetConnectionString("Geopolitics");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Geopolitics must be configured.");
        }

        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            return connectionString;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;

        // An in-memory database, a URI filename, and an already-absolute path each mean what they
        // say. Rooting any of them would change which database the host opens.
        if (string.IsNullOrWhiteSpace(source)
            || builder.Mode is SqliteOpenMode.Memory
            || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || source.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(source))
        {
            return connectionString;
        }

        builder.DataSource = Path.GetFullPath(source, contentRoot);
        return builder.ToString();
    }
}
