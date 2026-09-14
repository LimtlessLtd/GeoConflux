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
        ProviderLatency = meter.CreateHistogram<double>("ingestion.provider.latency", "ms", "Wall-clock time for one poll of an external provider.");
        ProviderFailures = meter.CreateCounter<long>("ingestion.provider.failures", "{poll}", "Provider polls that produced nothing because the provider could not be read.");
        ItemsProcessed = meter.CreateCounter<long>("pipeline.items.processed", "{item}", "Observations that completed processing.");
        ItemsFailedInPipeline = meter.CreateCounter<long>("pipeline.items.failed", "{item}", "Observations that failed during processing.");
        ItemsDeduplicated = meter.CreateCounter<long>("events.deduplicated", "{item}", "Observations rejected as exact re-deliveries.");
        IncidentsCreated = meter.CreateCounter<long>("events.created", "{incident}", "Incidents opened by the pipeline.");
        IncidentsCorrelated = meter.CreateCounter<long>("events.correlated", "{incident}", "Observations linked to an existing incident.");
        ClaimsHeld = meter.CreateCounter<long>("claims.held", "{claim}", "User-generated claims stored without an incident, awaiting a second source.");
        ClaimsReleased = meter.CreateCounter<long>("claims.released", "{claim}", "Held claims a later source corroborated.");
        GeocodingSuccess = meter.CreateCounter<long>("geocoding.success", "{resolution}", "Observations given authoritative coordinates.");
        GeocodingFailure = meter.CreateCounter<long>("geocoding.failure", "{resolution}", "Observations left without coordinates.");
        AiRequests = meter.CreateCounter<long>("ai.requests", "{request}", "Enrichment attempts sent to a provider.");
        AiFailures = meter.CreateCounter<long>("ai.failures", "{request}", "Enrichment attempts where the provider errored or timed out.");
        AiValidationFailures = meter.CreateCounter<long>("ai.validation_failures", "{response}", "Provider responses rejected by schema validation.");
        AiRepairAttempts = meter.CreateCounter<long>("ai.repair_attempts", "{request}", "Follow-up calls asking a provider to correct invalid output.");
        AiLatency = meter.CreateHistogram<double>("ai.latency", "ms", "Wall-clock time for one enrichment attempt.");
        Publications = meter.CreateCounter<long>("signalr.publications", "{message}", "Realtime messages published after persistence.");
        PublicationFailures = meter.CreateCounter<long>("signalr.publication_failures", "{message}", "Realtime publications that failed after a successful save.");
        ProcessingDuration = meter.CreateHistogram<double>("pipeline.processing.duration", "ms", "End-to-end processing time for one observation.");
        StageDuration = meter.CreateHistogram<double>("pipeline.stage.duration", "ms", "Wall-clock time for one pipeline stage, tagged by stage name.");
        ModelPredictions = meter.CreateCounter<long>("ml.severity.predictions", "{prediction}", "Severity predictions recorded against an observation.");
        ModelDisagreements = meter.CreateCounter<long>("ml.severity.disagreements", "{prediction}", "Predictions that differed from the severity the pipeline applied.");
        ModelFailures = meter.CreateCounter<long>("ml.severity.failures", "{prediction}", "Prediction attempts that failed; the observation continued without a second opinion.");
    }

    /// <summary>
    /// Names of the stages a single observation passes through, used as both span names and the
    /// <c>stage</c> tag on <see cref="StageDuration"/> so a trace and a metric can be read against
    /// each other rather than describing the pipeline in two different vocabularies.
    /// </summary>
    public static class Stages
    {
        public const string Deduplicate = "pipeline.deduplicate";
        public const string Enrich = "pipeline.enrich";
        public const string ScoreSeverity = "pipeline.score_severity";
        public const string ResolveLocation = "pipeline.resolve_location";
        public const string AssignConflict = "pipeline.assign_conflict";
        public const string Correlate = "pipeline.correlate";
        public const string Persist = "pipeline.persist";
        public const string Publish = "pipeline.publish";
    }

    /// <summary>
    /// Starts a child span for one stage and records its duration when disposed.
    /// <para>
    /// Both, from one call, deliberately. A span without a matching metric cannot be aggregated
    /// across a run, and a metric without a span cannot be attributed to the observation that caused
    /// it — and keeping the two in separate call sites is how they drift until the trace says a
    /// stage is slow and the dashboard says it is not.
    /// </para>
    /// </summary>
    public StageScope StartStage(string stageName) => new(this, stageName);

    /// <summary>Span and timer for one stage. Ends both on dispose, including on the exception path.</summary>
    public readonly struct StageScope : IDisposable
    {
        private readonly PipelineDiagnostics diagnostics;
        private readonly Activity? activity;
        private readonly long startedAt;
        private readonly string stageName;

        internal StageScope(PipelineDiagnostics diagnostics, string stageName)
        {
            this.diagnostics = diagnostics;
            this.stageName = stageName;
            activity = diagnostics.ActivitySource.StartActivity(stageName, ActivityKind.Internal);
            startedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>Adds detail to the stage span. A no-op when nothing is listening, as spans are.</summary>
        public void Tag(string key, object? value) => activity?.SetTag(key, value);

        /// <summary>
        /// Marks the stage as failed on the span. The metric is still recorded: how long a stage took
        /// before it failed is one of the more useful things to know about it.
        /// </summary>
        public void Fail(string reason) => activity?.SetStatus(ActivityStatusCode.Error, reason);

        public void Dispose()
        {
            diagnostics.StageDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", stageName));

            activity?.Dispose();
        }
    }

    public ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public Counter<long> ItemsReceived { get; }

    public Counter<long> ItemsFailed { get; }

    /// <summary>
    /// Poll duration tagged by provider and outcome. Latency and failures are kept separate from
    /// the pipeline counters because a slow provider and a slow pipeline call for different fixes.
    /// </summary>
    public Histogram<double> ProviderLatency { get; }

    public Counter<long> ProviderFailures { get; }

    public Counter<long> ItemsProcessed { get; }

    public Counter<long> ItemsFailedInPipeline { get; }

    public Counter<long> ItemsDeduplicated { get; }

    public Counter<long> IncidentsCreated { get; }

    public Counter<long> IncidentsCorrelated { get; }

    /// <summary>
    /// Claims the corroboration gate declined to turn into incidents. Worth a counter of its own
    /// because the gate is silent by design: it produces no error and no gap in the feed, so a rule
    /// that started holding everything would otherwise look exactly like a quiet week.
    /// </summary>
    public Counter<long> ClaimsHeld { get; }

    /// <summary>The other half of that figure. Held against released is how well Tier B is paying.</summary>
    public Counter<long> ClaimsReleased { get; }

    public Counter<long> GeocodingSuccess { get; }

    public Counter<long> GeocodingFailure { get; }

    public Counter<long> AiRequests { get; }

    public Counter<long> AiFailures { get; }

    public Counter<long> AiValidationFailures { get; }

    public Counter<long> AiRepairAttempts { get; }

    public Histogram<double> AiLatency { get; }

    public Counter<long> Publications { get; }

    public Counter<long> PublicationFailures { get; }

    public Histogram<double> ProcessingDuration { get; }

    /// <summary>
    /// Per-stage duration, tagged by stage. Separate from <see cref="ProcessingDuration"/> because
    /// the end-to-end figure answers "is the pipeline slow" and this one answers "which part".
    /// </summary>
    public Histogram<double> StageDuration { get; }

    public Counter<long> ModelPredictions { get; }

    /// <summary>
    /// How often the trained model differed from the severity applied. Worth a counter rather than
    /// only a log line: a rate that moves is the signal the model has drifted from the traffic it is
    /// seeing, and that is a trend, not an event.
    /// </summary>
    public Counter<long> ModelDisagreements { get; }

    public Counter<long> ModelFailures { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        meter.Dispose();
    }
}
