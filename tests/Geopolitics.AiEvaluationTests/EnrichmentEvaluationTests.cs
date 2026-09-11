using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Geopolitics.AiEvaluationTests;

/// <summary>
/// Measures the enrichment path against labelled fixtures instead of assuming it works.
/// <para>
/// The harness evaluates <em>whichever provider is configured</em>. By default that is the
/// deterministic in-process stand-in, so a CI run measures the offline baseline — the keyword
/// classifier and the script-based language detector — with no credentials and no network. Pointing
/// <c>GEOCONFLUX_EVAL_PROVIDER</c> at a real provider runs the identical fixtures and scoring
/// against a language model.
/// </para>
/// <para>
/// The asserted thresholds are regression guards set below a previously measured baseline, not
/// quality targets and not claims about the state of the art. Every figure in the emitted report
/// comes from the run that produced it; none is transcribed from anywhere else.
/// </para>
/// </summary>
public sealed class EnrichmentEvaluationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Floors for the deterministic stand-in, taken from a measured run and rounded down. They exist
    /// to fail the build when a change makes classification worse, so they deliberately sit a little
    /// under the observed figures rather than tracking them exactly.
    /// </summary>
    /// <summary>Property names that would mean a position had leaked into the output.</summary>
    private static readonly string[] CoordinateFieldNames =
        ["latitude", "longitude", "lat", "lon", "lng", "coordinates"];

    private const double MinimumLanguageAccuracy = 0.90;
    private const double MinimumEventTypeMacroF1 = 0.60;
    private const double MinimumSeverityMacroF1 = 0.65;
    private const double MinimumLocationF1 = 0.75;

    /// <summary>
    /// Deliberately near the floor of the scale. The offline stand-in extracts entities by finding
    /// capitalised runs, which recovers most of the right names and a great deal of noise besides.
    /// Setting a flattering threshold here would hide that; the number is low because the capability
    /// is weak, and it is asserted only so the weak capability cannot silently become no capability.
    /// </summary>
    private const double MinimumEntityF1 = 0.08;

    [Fact]
    public async Task TheConfiguredProviderIsEvaluatedAgainstTheLabelledFixtures()
    {
        var dataset = EvaluationDataset.Load();
        var options = ResolveProviderOptions();
        using var diagnostics = new PipelineDiagnostics(new EvaluationMeterFactory());
        using var chatClient = BuildChatClient(options);

        var service = new ChatClientEnrichmentService(
            chatClient,
            Options.Create(new EnrichmentOptions { Timeout = TimeSpan.FromSeconds(60) }),
            Options.Create(options),
            diagnostics,
            TimeProvider.System,
            NullLogger<ChatClientEnrichmentService>.Instance);

        var eventTypes = new ConfusionMatrix();
        var severities = new ConfusionMatrix();
        var languages = new ConfusionMatrix();
        var locations = new SetScorer();
        var entities = new SetScorer();
        var latencies = new List<double>(dataset.Cases.Count);
        var structuredOutputSuccesses = 0;
        var failures = new List<string>();

        foreach (var testCase in dataset.Cases)
        {
            var result = await service.EnrichAsync(
                new EnrichmentRequest(testCase.SourceName, testCase.Title, testCase.Content),
                CancellationToken.None);

            latencies.Add(result.LatencyMilliseconds);

            if (result.Enrichment is not { } enrichment)
            {
                // A failed enrichment is a real outcome and is counted as one, not skipped. Dropping
                // it would let a provider that answers rarely but well post an excellent score.
                failures.Add($"{testCase.Id}: {result.Outcome} — {result.Error}");
                eventTypes.Record(testCase.Expected.EventType, "«no output»");
                severities.Record(testCase.Expected.Severity, "«no output»");
                languages.Record(testCase.Expected.Language, "«no output»");
                locations.Record(Expected(testCase.Expected.LocationName), []);
                entities.Record(testCase.Expected.Entities, []);
                continue;
            }

            structuredOutputSuccesses++;
            eventTypes.Record(testCase.Expected.EventType, EnrichmentContract.ToWire(enrichment.EventType));
            severities.Record(testCase.Expected.Severity, EnrichmentContract.ToWire(enrichment.Severity));
            languages.Record(testCase.Expected.Language, enrichment.Language ?? "«none»");
            locations.Record(Expected(testCase.Expected.LocationName), Expected(enrichment.LocationName));
            entities.Record(testCase.Expected.Entities, [.. enrichment.Entities.Select(entity => entity.Name)]);
        }

        var structuredOutputRate = (double)structuredOutputSuccesses / dataset.Cases.Count;
        var report = BuildReport(
            dataset,
            options,
            structuredOutputRate,
            latencies,
            languages,
            eventTypes,
            severities,
            locations.Result,
            entities.Result,
            failures);

        output.WriteLine(report);
        WriteReport(report);

        // A provider that cannot satisfy its own output contract is not usable, whichever it is.
        Assert.True(
            structuredOutputRate >= 0.9,
            $"Structured output succeeded on {structuredOutputRate:P0} of cases, which is below the 90% floor.");

        if (options.Provider != AiProviderKind.Mock)
        {
            // A live model is non-deterministic, so asserting a tight score against it would produce
            // a test that fails for reasons unrelated to the change being tested. The run still
            // publishes its full report; only the quality thresholds are left unenforced.
            return;
        }

        Assert.True(
            languages.Accuracy >= MinimumLanguageAccuracy,
            $"Language accuracy {languages.Accuracy:F2} fell below the {MinimumLanguageAccuracy:F2} regression floor.");
        Assert.True(
            eventTypes.MacroF1() >= MinimumEventTypeMacroF1,
            $"Event-type macro F1 {eventTypes.MacroF1():F2} fell below the {MinimumEventTypeMacroF1:F2} regression floor.");
        Assert.True(
            severities.MacroF1() >= MinimumSeverityMacroF1,
            $"Severity macro F1 {severities.MacroF1():F2} fell below the {MinimumSeverityMacroF1:F2} regression floor.");
        Assert.True(
            locations.Result.F1 >= MinimumLocationF1,
            $"Location F1 {locations.Result.F1:F2} fell below the {MinimumLocationF1:F2} regression floor.");
        Assert.True(
            entities.Result.F1 >= MinimumEntityF1,
            $"Entity F1 {entities.Result.F1:F2} fell below the {MinimumEntityF1:F2} regression floor.");
    }

    [Fact]
    public async Task TheDeterministicProviderProducesIdenticalOutputOnRepeatedRuns()
    {
        // The offline baseline must be reproducible, or a shifting score could not be read as a
        // regression. This is the property that makes the thresholds above meaningful.
        var dataset = EvaluationDataset.Load();
        using var diagnostics = new PipelineDiagnostics(new EvaluationMeterFactory());
        using var chatClient = new DeterministicMockChatClient(new KeywordEventClassifier());

        var service = new ChatClientEnrichmentService(
            chatClient,
            Options.Create(new EnrichmentOptions()),
            Options.Create(new AiProviderOptions { Provider = AiProviderKind.Mock }),
            diagnostics,
            TimeProvider.System,
            NullLogger<ChatClientEnrichmentService>.Instance);

        foreach (var testCase in dataset.Cases)
        {
            var request = new EnrichmentRequest(testCase.SourceName, testCase.Title, testCase.Content);
            var first = await service.EnrichAsync(request, CancellationToken.None);
            var second = await service.EnrichAsync(request, CancellationToken.None);

            Assert.Equal(first.StructuredOutput, second.StructuredOutput);
        }
    }

    [Fact]
    public async Task NoEnrichmentOutputEverCarriesACoordinate()
    {
        // ADR 005 as an assertion rather than a convention. The contract has no coordinate field, so
        // this checks the whole path end to end, including the prompt-injection fixture that asks
        // the model to behave differently.
        var dataset = EvaluationDataset.Load();
        using var diagnostics = new PipelineDiagnostics(new EvaluationMeterFactory());
        using var chatClient = new DeterministicMockChatClient(new KeywordEventClassifier());

        var service = new ChatClientEnrichmentService(
            chatClient,
            Options.Create(new EnrichmentOptions()),
            Options.Create(new AiProviderOptions { Provider = AiProviderKind.Mock }),
            diagnostics,
            TimeProvider.System,
            NullLogger<ChatClientEnrichmentService>.Instance);

        foreach (var testCase in dataset.Cases)
        {
            var result = await service.EnrichAsync(
                new EnrichmentRequest(testCase.SourceName, testCase.Title, testCase.Content),
                CancellationToken.None);

            if (result.StructuredOutput is { } json)
            {
                // Checked against the property names, not the text. A summary can legitimately
                // contain the word "latitude" because the source report did.
                using var document = System.Text.Json.JsonDocument.Parse(json);

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    Assert.DoesNotContain(
                        property.Name.ToLowerInvariant(),
                        CoordinateFieldNames);
                }
            }
        }
    }

    [Fact]
    public async Task APromptInjectionFixtureDoesNotEscapeTheOutputContract()
    {
        // The guarantee is not that a model ignores injected instructions — it might not. It is that
        // whatever it returns is still forced through schema validation before anything is adopted.
        var dataset = EvaluationDataset.Load();
        var injection = dataset.Cases.Single(testCase => testCase.Id == "inj-001");
        using var diagnostics = new PipelineDiagnostics(new EvaluationMeterFactory());
        using var chatClient = new DeterministicMockChatClient(new KeywordEventClassifier());

        var service = new ChatClientEnrichmentService(
            chatClient,
            Options.Create(new EnrichmentOptions()),
            Options.Create(new AiProviderOptions { Provider = AiProviderKind.Mock }),
            diagnostics,
            TimeProvider.System,
            NullLogger<ChatClientEnrichmentService>.Instance);

        var result = await service.EnrichAsync(
            new EnrichmentRequest(injection.SourceName, injection.Title, injection.Content),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.InRange(result.Enrichment!.Confidence, 0, 1);
        Assert.Contains(EnrichmentContract.ToWire(result.Enrichment.EventType), EnrichmentContract.EventTypeNames);
        Assert.Contains(EnrichmentContract.ToWire(result.Enrichment.Severity), EnrichmentContract.SeverityNames);
    }

    private static IReadOnlyList<string> Expected(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];

    /// <summary>
    /// Reads provider settings from the environment so a live evaluation needs no code change and no
    /// committed credential. Absent configuration means the offline stand-in, which is what CI uses.
    /// </summary>
    private static AiProviderOptions ResolveProviderOptions()
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

    private static IChatClient BuildChatClient(AiProviderOptions options) =>
        options.Provider == AiProviderKind.Mock
            ? new DeterministicMockChatClient(new KeywordEventClassifier())
            : ChatClientFactory.Create(options, new EvaluationServiceProvider());

    private static string BuildReport(
        EvaluationDataset dataset,
        AiProviderOptions options,
        double structuredOutputRate,
        List<double> latencies,
        ConfusionMatrix languages,
        ConfusionMatrix eventTypes,
        ConfusionMatrix severities,
        SetMetrics locations,
        SetMetrics entities,
        List<string> failures)
    {
        latencies.Sort();
        var builder = new StringBuilder();
        var isMock = options.Provider == AiProviderKind.Mock;

        builder.AppendLine("# AI evaluation results");
        builder.AppendLine();
        builder.AppendLine("<!-- Generated by Geopolitics.AiEvaluationTests. Do not edit by hand. -->");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- Provider: `{options.Provider}`");
        builder.AppendLine(isMock ? " (deterministic in-process stand-in — **not a language model**)" : string.Empty);
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Model: `{(isMock ? DeterministicMockChatClient.ModelName : options.Model)}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Prompt version: `{EnrichmentPrompt.Version}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Schema version: `{EnrichmentContract.SchemaVersion}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Dataset version: `{dataset.DatasetVersion}` ({dataset.Cases.Count} labelled cases)");
        builder.AppendLine();

        if (isMock)
        {
            builder.AppendLine("> These figures measure the **offline baseline**: a keyword classifier and a");
            builder.AppendLine("> script-based language detector, wrapped in the same schema-validated path a real");
            builder.AppendLine("> provider uses. They are reproducible and they are what CI enforces. They say");
            builder.AppendLine("> nothing about how any language model performs — to measure that, set");
            builder.AppendLine("> `GEOCONFLUX_EVAL_PROVIDER` and rerun.");
        }
        else
        {
            builder.AppendLine("> These figures come from a live provider and are **not reproducible**: the same");
            builder.AppendLine("> fixtures rerun may score differently. Temperature is 0 and a seed is sent where");
            builder.AppendLine("> the provider supports it, which reduces variation without removing it.");
        }

        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();
        builder.AppendLine("| Measure | Value |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Structured output success rate | {structuredOutputRate:P1} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Language accuracy | {languages.Accuracy:F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Event type — accuracy | {eventTypes.Accuracy:F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Event type — macro F1 | {eventTypes.MacroF1():F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Severity — accuracy | {severities.Accuracy:F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Severity — macro F1 | {severities.MacroF1():F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Location name — precision / recall / F1 | {locations.Precision:F2} / {locations.Recall:F2} / {locations.F1:F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Entities — precision / recall / F1 | {entities.Precision:F2} / {entities.Recall:F2} / {entities.F1:F2} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| Latency median / p95 | {Percentile(latencies, 0.5):F1} ms / {Percentile(latencies, 0.95):F1} ms |");
        builder.AppendLine();
        builder.AppendLine("## Event type");
        builder.AppendLine();
        builder.Append(eventTypes.ToMarkdownTable());
        builder.AppendLine();
        builder.AppendLine("## Severity");
        builder.AppendLine();
        builder.Append(severities.ToMarkdownTable());

        if (failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Cases with no usable output");
            builder.AppendLine();

            foreach (var failure in failures)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {failure}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Reading these numbers");
        builder.AppendLine();
        builder.AppendLine("The fixtures are synthetic, written for this repository, and labelled by its author");
        builder.AppendLine("rather than by a domain expert or by consensus. Sixteen cases is far too few for a");
        builder.AppendLine("confidence interval worth quoting. What the set is good for is detecting a regression:");
        builder.AppendLine("if a prompt or classifier change makes these scores drop, something got worse.");
        builder.AppendLine("Treat any single figure here as a smoke test, not as a measurement of geopolitical");
        builder.AppendLine("classification ability.");

        return builder.ToString();
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>
    /// Writes the report next to the fixtures when the repository layout can be found, so the
    /// committed results are always the output of a real run rather than hand-written prose.
    /// </summary>
    private static void WriteReport(string report)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "data", "ai-evaluation");

            if (Directory.Exists(candidate))
            {
                File.WriteAllText(Path.Combine(candidate, "RESULTS.md"), report);
                return;
            }

            directory = directory.Parent;
        }
    }

    private sealed class EvaluationMeterFactory : IMeterFactory
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
        }
    }

    /// <summary>Supplies the one service <see cref="ChatClientFactory"/> resolves for a live provider.</summary>
    private sealed class EvaluationServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(Microsoft.Extensions.Logging.ILoggerFactory) ? NullLoggerFactory.Instance : null;
    }
}
