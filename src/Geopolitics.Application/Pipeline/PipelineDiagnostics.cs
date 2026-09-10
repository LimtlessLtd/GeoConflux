using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Instruments the pipeline so a single observation can be followed from ingestion through
/// correlation, persistence, and realtime delivery. Registered as a singleton and shared by every
/// stage; the meter and activity source names are what an OpenTelemetry exporter subscribes to.
/// </summary>
public sealed class PipelineDiagnostics : IDisposable
{
    public const string MeterName = "Geopolitics.Pipeline";
    public const string ActivitySourceName = "Geopolitics.Pipeline";

    private readonly Meter meter;

    public PipelineDiagnostics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        meter = meterFactory.Create(MeterName);
        ItemsReceived = meter.CreateCounter<long>("ingestion.items.received", "{item}", "Envelopes accepted from an ingestion source.");
        ItemsFailed = meter.CreateCounter<long>("ingestion.items.failed", "{item}", "Envelopes rejected before reaching the queue.");
        ItemsProcessed = meter.CreateCounter<long>("pipeline.items.processed", "{item}", "Observations that completed processing.");
        ItemsFailedInPipeline = meter.CreateCounter<long>("pipeline.items.failed", "{item}", "Observations that failed during processing.");
        ItemsDeduplicated = meter.CreateCounter<long>("events.deduplicated", "{item}", "Observations rejected as exact re-deliveries.");
        IncidentsCreated = meter.CreateCounter<long>("events.created", "{incident}", "Incidents opened by the pipeline.");
        IncidentsCorrelated = meter.CreateCounter<long>("events.correlated", "{incident}", "Observations linked to an existing incident.");
        GeocodingSuccess = meter.CreateCounter<long>("geocoding.success", "{resolution}", "Observations given authoritative coordinates.");
        GeocodingFailure = meter.CreateCounter<long>("geocoding.failure", "{resolution}", "Observations left without coordinates.");
        Publications = meter.CreateCounter<long>("signalr.publications", "{message}", "Realtime messages published after persistence.");
        PublicationFailures = meter.CreateCounter<long>("signalr.publication_failures", "{message}", "Realtime publications that failed after a successful save.");
        ProcessingDuration = meter.CreateHistogram<double>("pipeline.processing.duration", "ms", "End-to-end processing time for one observation.");
    }

    public ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public Counter<long> ItemsReceived { get; }

    public Counter<long> ItemsFailed { get; }

    public Counter<long> ItemsProcessed { get; }

    public Counter<long> ItemsFailedInPipeline { get; }

    public Counter<long> ItemsDeduplicated { get; }

    public Counter<long> IncidentsCreated { get; }

    public Counter<long> IncidentsCorrelated { get; }

    public Counter<long> GeocodingSuccess { get; }

    public Counter<long> GeocodingFailure { get; }

    public Counter<long> Publications { get; }

    public Counter<long> PublicationFailures { get; }

    public Histogram<double> ProcessingDuration { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        meter.Dispose();
    }
}
