using System.Diagnostics;
using System.Diagnostics.Metrics;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// Verifies that the pipeline actually emits the telemetry it claims to.
/// <para>
/// Instrumentation is the code most likely to be silently wrong, because nothing fails when it is.
/// A span that is never started, a metric that is recorded under a name no exporter subscribes to,
/// or a stage timer that never fires all look identical to a working system from the inside — and
/// are discovered during the incident the telemetry existed for.
/// </para>
/// </summary>
public sealed class ObservabilityTests
{
    private const string ModelVersion = "observability-test/v1";

    [Fact]
    public async Task EveryPipelineStageEmitsItsOwnSpanUnderTheProcessingSpan()
    {
        using var trace = new TraceRecorder();

        var harness = new PipelineTestHarness
        {
            SeverityModel = new ScriptedSeverityModel(Severity.High, 0.7),
        };

        await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "An explosion damaged a tanker in the Gulf of Aden. Two crew were injured.",
                DeclaredLocationName = "Gulf of Aden",
            },
            CancellationToken.None);

        var spans = trace.Spans;
        var names = spans.Select(span => span.OperationName).ToArray();

        // One span per stage, so a trace answers "which part was slow" rather than only "it was slow".
        Assert.Contains(PipelineDiagnostics.Stages.Deduplicate, names);
        Assert.Contains(PipelineDiagnostics.Stages.Enrich, names);
        Assert.Contains(PipelineDiagnostics.Stages.ScoreSeverity, names);
        Assert.Contains(PipelineDiagnostics.Stages.ResolveLocation, names);
        Assert.Contains(PipelineDiagnostics.Stages.Correlate, names);
        Assert.Contains(PipelineDiagnostics.Stages.Persist, names);
        Assert.Contains(PipelineDiagnostics.Stages.Publish, names);

        // All of them children of the one processing span. Stage spans that started their own trace
        // would be unjoinable to the observation that caused them, which is the whole point.
        var root = spans.Single(span => span.OperationName == "pipeline.process");
        var stageSpans = spans.Where(span => span.OperationName.StartsWith("pipeline.", StringComparison.Ordinal)
            && span != root);

        Assert.NotEmpty(stageSpans);
        Assert.All(stageSpans, span => Assert.Equal(root.TraceId, span.TraceId));
    }

    /// <summary>
    /// The stages are siblings, not a chain.
    /// <para>
    /// Sharing a trace identifier is not enough, which is why this is separate from the test above.
    /// A stage whose scope encloses the stages after it reports their cost as its own: the span
    /// nests wrongly in a trace, and — because the same scope records the duration metric — the
    /// <c>stage</c> histogram attributes persistence and publication to correlation. The per-stage
    /// breakdown then sums to more than the end-to-end figure it is meant to decompose, which is the
    /// one thing a cost breakdown must never do.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EachStageIsMeasuredOnItsOwnRatherThanInsideTheStageBeforeIt()
    {
        using var trace = new TraceRecorder();

        var harness = new PipelineTestHarness
        {
            SeverityModel = new ScriptedSeverityModel(Severity.High, 0.7),
        };

        await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "An explosion damaged a tanker in the Gulf of Aden. Two crew were injured.",
                DeclaredLocationName = "Gulf of Aden",
            },
            CancellationToken.None);

        var spans = trace.Spans;
        var root = spans.Single(span => span.OperationName == "pipeline.process");

        string[] stages =
        [
            PipelineDiagnostics.Stages.Deduplicate,
            PipelineDiagnostics.Stages.Enrich,
            PipelineDiagnostics.Stages.ScoreSeverity,
            PipelineDiagnostics.Stages.ResolveLocation,
            PipelineDiagnostics.Stages.Correlate,
            PipelineDiagnostics.Stages.Persist,
            PipelineDiagnostics.Stages.Publish,
        ];

        foreach (var stage in stages)
        {
            var span = spans.Single(value => value.OperationName == stage);
            Assert.Equal(root.SpanId, span.ParentSpanId);
        }
    }

    [Fact]
    public async Task StageSpansCarryTheDecisionTheStageMade()
    {
        using var trace = new TraceRecorder();

        var harness = new PipelineTestHarness
        {
            SeverityModel = new ScriptedSeverityModel(Severity.Critical, 0.9, "trace-model/v1"),
        };

        // Made to succeed, because the tag under test is the one that says it did. The harness
        // resolver fails by default, which would make this assertion pass for the wrong reason.
        harness.LocationResolver.Behaviour = _ => new LocationResolution(
            new GeoLocation("Red Sea", null, 20.0, 38.0),
            LocationResolutionMethod.Gazetteer,
            0.8,
            null);

        await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "A patrol reported a quiet night along the contact line.",
                DeclaredLocationName = "Red Sea",
                DeclaredSeverity = Severity.Low,
            },
            CancellationToken.None);

        var spans = trace.Spans;

        // A span with no attributes tells you a stage ran, which you already knew. The tags are what
        // make a trace diagnostic rather than decorative.
        var locate = spans.Single(span => span.OperationName == PipelineDiagnostics.Stages.ResolveLocation);
        Assert.Equal("True", locate.GetTagItem("location.resolved")?.ToString());

        var score = spans.Single(span => span.OperationName == PipelineDiagnostics.Stages.ScoreSeverity);
        Assert.Equal("trace-model/v1", score.GetTagItem("model.version"));
        Assert.Equal("True", score.GetTagItem("model.disagrees")?.ToString());

        var correlate = spans.Single(span => span.OperationName == PipelineDiagnostics.Stages.Correlate);
        Assert.NotNull(correlate.GetTagItem("incident.id"));
    }

    [Fact]
    public async Task StageDurationsAndModelCountersAreRecorded()
    {
        var stages = new List<string>();
        var predictions = 0L;
        var disagreements = 0L;

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PipelineDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "pipeline.stage.duration")
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "stage" && tag.Value is string stage)
                    {
                        lock (stages)
                        {
                            stages.Add(stage);
                        }
                    }
                }
            }
        });

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var mine = false;

            foreach (var tag in tags)
            {
                if (tag.Key == "model" && (tag.Value as string) == ModelVersion)
                {
                    mine = true;
                }
            }

            if (instrument.Name == "ml.severity.predictions" && mine)
            {
                Interlocked.Add(ref predictions, measurement);
            }
            else if (instrument.Name == "ml.severity.disagreements")
            {
                // Disagreements are tagged by severity rather than by model, so they cannot be
                // attributed the same way. Counted as at-least-one instead of exactly-one.
                Interlocked.Add(ref disagreements, measurement);
            }
        });

        meterListener.Start();

        var harness = new PipelineTestHarness
        {
            // A version unique to this test, so its counters can be told apart from those of any
            // other test the runner happens to schedule alongside it. A MeterListener, like an
            // ActivityListener, is process-wide.
            SeverityModel = new ScriptedSeverityModel(Severity.Critical, 0.9, ModelVersion),
        };

        await harness.BuildProcessor().ProcessAsync(
            new ObservationEnvelope
            {
                SourceName = "fixture",
                Kind = ObservationKind.News,
                Content = "Shelling damaged buildings in a border village overnight.",
                DeclaredSeverity = Severity.Medium,
            },
            CancellationToken.None);

        meterListener.Dispose();

        // Tagged by stage, using the same names the spans use, so a metric and a trace can be read
        // against each other instead of describing the pipeline in two vocabularies.
        Assert.Contains(PipelineDiagnostics.Stages.Enrich, stages);
        Assert.Contains(PipelineDiagnostics.Stages.Correlate, stages);
        Assert.Contains(PipelineDiagnostics.Stages.Persist, stages);

        Assert.Equal(1, predictions);

        // A disagreement rate is a trend, not an event, which is why it is a counter and not only a
        // log line: a rate that moves is the signal the model has drifted from the traffic.
        Assert.True(disagreements >= 1);
    }
}

/// <summary>
/// Collects pipeline spans belonging to one test, and only that test.
/// <para>
/// An <see cref="ActivityListener"/> is process-wide, so a listener that simply collects everything
/// from the pipeline source also collects whatever another test is doing at the same moment — which
/// passes in isolation and fails under a parallel runner, having asserted nothing either way.
/// </para>
/// <para>
/// The fix is to own a root activity and keep only the spans beneath it. Every pipeline span nests
/// under <see cref="Activity.Current"/>, so the root's trace identifier is the thing that separates
/// this test's work from anyone else's.
/// </para>
/// </summary>
internal sealed class TraceRecorder : IDisposable
{
    private const string TestSourceName = "Geopolitics.Tests.Trace";

    private readonly ActivitySource source = new(TestSourceName);
    private readonly ActivityListener listener;
    private readonly List<Activity> collected = [];
    private readonly Lock gate = new();
    private readonly Activity? root;

    public TraceRecorder()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = candidate =>
                candidate.Name == PipelineDiagnostics.ActivitySourceName || candidate.Name == TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (gate)
                {
                    collected.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);

        // Started after the listener is registered: without a listener, StartActivity returns null
        // and there would be no trace to filter on.
        root = source.StartActivity("test.root", ActivityKind.Internal);
    }

    /// <summary>Pipeline spans from this test's trace, in the order they completed.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (gate)
            {
                return [.. collected.Where(activity =>
                    activity.TraceId == root?.TraceId
                    && activity.Source.Name == PipelineDiagnostics.ActivitySourceName)];
            }
        }
    }

    public void Dispose()
    {
        root?.Dispose();
        listener.Dispose();
        source.Dispose();
    }
}
