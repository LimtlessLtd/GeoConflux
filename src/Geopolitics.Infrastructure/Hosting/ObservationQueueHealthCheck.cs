using Geopolitics.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Geopolitics.Infrastructure.Hosting;

/// <summary>
/// Reports queue saturation. A persistently full queue means producers are being throttled and
/// observations are arriving faster than they can be processed, which is a real operational signal
/// rather than an outage — hence degraded rather than unhealthy.
/// </summary>
public sealed class ObservationQueueHealthCheck(IObservationQueueMonitor monitor) : IHealthCheck
{
    private const double DegradedThreshold = 0.8;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var depth = monitor.Depth;
        var capacity = Math.Max(1, monitor.Capacity);
        var utilisation = (double)depth / capacity;

        var data = new Dictionary<string, object>
        {
            ["depth"] = depth,
            ["capacity"] = capacity,
            ["utilisation"] = Math.Round(utilisation, 3),
            ["totalEnqueued"] = monitor.TotalEnqueued,
        };

        return Task.FromResult(utilisation >= DegradedThreshold
            ? HealthCheckResult.Degraded($"The processing queue is {utilisation:P0} full.", data: data)
            : HealthCheckResult.Healthy($"The processing queue is {utilisation:P0} full.", data));
    }
}
