using Geopolitics.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Hosts the real API over a throwaway SQLite file.
/// <para>
/// A file rather than an in-memory provider on purpose: the deduplication guarantee depends on a
/// filtered unique index, and an in-memory provider would not enforce it, so the test would pass
/// while the production behaviour it claims to cover went unverified.
/// </para>
/// </summary>
/// <param name="settings">
/// Extra configuration applied last, so a test can override any default this factory sets.
/// </param>
/// <param name="chatClient">
/// Replaces the configured provider. Supplied when a test needs a provider that can read the text
/// it is given, which the offline stand-in deliberately cannot.
/// </param>
/// <param name="configureServices">
/// Applied last, after every default registration. Tests that need to make a real dependency
/// misbehave — a save that fails, a model that throws — substitute it here rather than reaching for
/// a parallel host, so what runs is still the composed application.
/// </param>
public sealed class PipelineFactory(
    bool runPipeline,
    bool runSources,
    IReadOnlyDictionary<string, string?>? settings = null,
    IChatClient? chatClient = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"geoconflux-tests-{Guid.NewGuid():N}.db");

    /// <summary>Records everything the pipeline published, so realtime behaviour is assertable.</summary>
    public RecordingNotifier Notifier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Geopolitics"] = $"Data Source={databasePath}",

                // Recorded delays are removed so the test finishes in milliseconds while still
                // exercising the real ordering of the recorded stream.
                ["Replay:SpeedFactor"] = "0",
                ["Replay:Loop"] = "false",
                ["Pipeline:SourcesEnabled"] = runSources ? "true" : "false",
                ["Pipeline:ProcessorEnabled"] = runPipeline ? "true" : "false",

                // One worker keeps assertions about ordering deterministic, and avoids the
                // read-then-write race two workers have when simultaneous reports describe one event.
                ["Pipeline:ProcessorConcurrency"] = "1",

                // No credentials in tests: the deterministic stand-in is the provider, which is the
                // same default the application ships with.
                ["Ai:Provider"] = "Mock",
            }));

        if (settings is { Count: > 0 })
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IIncidentNotifier>();
            services.AddSingleton<IIncidentNotifier>(Notifier);

            if (chatClient is not null)
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton(chatClient);
            }

            configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // Best effort: a leftover temp file is harmless, and failing teardown would mask real results.
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The SQLite handle may not have been released yet.
            }
            catch (UnauthorizedAccessException)
            {
                // Same, on a locked file.
            }
        }
    }
}

/// <summary>Captures realtime publications for assertion without needing a SignalR client.</summary>
public sealed class RecordingNotifier : IIncidentNotifier
{
    private readonly Lock gate = new();
    private readonly List<Application.Contracts.IncidentResponse> created = [];
    private readonly List<Application.Contracts.IncidentResponse> updated = [];
    private readonly List<Application.Contracts.ObservationResponse> observations = [];

    public IReadOnlyList<Application.Contracts.IncidentResponse> Created
    {
        get { lock (gate) { return [.. created]; } }
    }

    public IReadOnlyList<Application.Contracts.IncidentResponse> Updated
    {
        get { lock (gate) { return [.. updated]; } }
    }

    public IReadOnlyList<Application.Contracts.ObservationResponse> Observations
    {
        get { lock (gate) { return [.. observations]; } }
    }

    public Task IncidentCreatedAsync(Application.Contracts.IncidentResponse incident, CancellationToken cancellationToken)
    {
        lock (gate) { created.Add(incident); }
        return Task.CompletedTask;
    }

    public Task IncidentUpdatedAsync(Application.Contracts.IncidentResponse incident, CancellationToken cancellationToken)
    {
        lock (gate) { updated.Add(incident); }
        return Task.CompletedTask;
    }

    public Task ObservationReceivedAsync(Application.Contracts.ObservationResponse observation, CancellationToken cancellationToken)
    {
        lock (gate) { observations.Add(observation); }
        return Task.CompletedTask;
    }
}
