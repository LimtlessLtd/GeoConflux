using System.Diagnostics.Metrics;
using Geopolitics.Application.Pipeline;
using Geopolitics.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Geopolitics.AiEvaluationTests;

/// <summary>
/// The minimum host an evaluation needs: a provider, a meter factory, and a service provider that
/// resolves logging and nothing else.
/// <para>
/// Shared between the enrichment harness and the severity-model harness on purpose. The comparison
/// between the two is only meaningful if both ran against the same provider resolved the same way;
/// two private copies of this would drift, and the report would quietly be comparing a model against
/// a differently-configured baseline.
/// </para>
/// </summary>
internal static class EvaluationHost
{
    /// <summary>
    /// The provider under evaluation. Defaults to the deterministic in-process stand-in, so a CI run
    /// measures the offline baseline with no credentials and no network.
    /// </summary>
    public static AiProviderOptions ResolveProviderOptions()
    {
        var provider = Environment.GetEnvironmentVariable("GEOCONFLUX_EVAL_PROVIDER");

        if (string.IsNullOrWhiteSpace(provider) || !Enum.TryParse<AiProviderKind>(provider, true, out var kind))
        {
            return new AiProviderOptions { Provider = AiProviderKind.Mock };
        }

        return new AiProviderOptions
        {
            Provider = kind,
            Model = Environment.GetEnvironmentVariable("GEOCONFLUX_EVAL_MODEL") ?? "llama3.2",
            Endpoint = Environment.GetEnvironmentVariable("GEOCONFLUX_EVAL_ENDPOINT"),
            ApiKey = Environment.GetEnvironmentVariable("GEOCONFLUX_EVAL_API_KEY"),
        };
    }

    public static IChatClient BuildChatClient(AiProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Provider == AiProviderKind.Mock
            ? new DeterministicMockChatClient(new KeywordEventClassifier())
            : ChatClientFactory.Create(options, new EvaluationServiceProvider());
    }

    /// <summary>
    /// Owns the meters an evaluation creates so they are disposed with it, rather than accumulating
    /// across a test run.
    /// </summary>
    public sealed class MeterFactory : IMeterFactory
    {
        private readonly List<Meter> meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
            meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in meters)
            {
                meter.Dispose();
            }

            meters.Clear();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class EvaluationServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILoggerFactory) ? NullLoggerFactory.Instance : null;
    }
}
