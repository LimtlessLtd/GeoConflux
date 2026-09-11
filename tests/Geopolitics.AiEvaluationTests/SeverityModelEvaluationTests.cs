using System.Globalization;
using System.Text;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Enrichment;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Ai;
using Geopolitics.Infrastructure.Ml;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Geopolitics.AiEvaluationTests;

/// <summary>
/// Scores the trained severity model against held-out labels, and against the enrichment path on the
/// same cases.
/// <para>
/// The comparison is the reason this exists. A language model's severity assessment on its own cannot
/// be judged — there is nothing to judge it against, and "it looks reasonable" is not a measurement.
/// Running a supervised model and the enrichment path over identical held-out cases produces three
/// figures that mean something next to each other: how often each agrees with the labels, and how
/// often they agree with each other.
/// </para>
/// <para>
/// What a good score here means is narrow and worth stating. The corpus is synthetic and
/// author-labelled, so the model is being asked to recover one person's reading of one rubric from
/// 95 examples. That is a real supervised-learning result and it is reported as one. It is not
/// evidence about real-world severity assessment, and the emitted report says so in its own body
/// rather than only here.
/// </para>
/// </summary>
public sealed class SeverityModelEvaluationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Regression floors, not quality targets.
    /// <para>
    /// Two things set the level. They sit clearly above what the holdout gives away for free — the
    /// majority class alone scores 0.31 — so passing means the model learned something. And they sit
    /// well under the measured figures, because SDCA is reproducible within a process but its
    /// floating-point arithmetic is not guaranteed identical across platforms, and a floor that
    /// tracked the observed number exactly would fail CI on a last-decimal difference rather than on
    /// a regression.
    /// </para>
    /// </summary>
    private const double MinimumAccuracy = 0.48;

    private const double MinimumMacroF1 = 0.42;

    /// <summary>
    /// The model must also beat the enrichment baseline it is being compared against. This is the
    /// assertion that would catch the failure the accuracy floors cannot: a model that still clears
    /// its own floor but has stopped being worth running.
    /// </summary>
    private const double MinimumMarginOverBaseline = 0.05;

    [Fact]
    public void TheHoldoutSetIsNeverTrainedOn()
    {
        var training = SeverityDataset.Training.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var holdout = SeverityDataset.Holdout.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(training);
        Assert.NotEmpty(holdout);

        // Asserted rather than assumed. A leak would raise every figure in the report while leaving
        // it looking entirely reasonable, which is the one failure mode an evaluation cannot tolerate.
        Assert.Empty(training.Intersect(holdout, StringComparer.Ordinal));

        // Identical text under two ids would leak just as effectively as a shared id.
        var trainingText = SeverityDataset.Training
            .Select(value => value.Features.Text)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(SeverityDataset.Holdout, value => trainingText.Contains(value.Features.Text));
    }

    [Fact]
    public void EveryClassIsRepresentedInBothSplits()
    {
        foreach (var severity in new[] { Severity.Low, Severity.Medium, Severity.High, Severity.Critical })
        {
            // A class absent from training cannot be predicted at all, and a class absent from the
            // holdout is scored as if it did not exist. Either makes the headline figures a fiction.
            Assert.Contains(SeverityDataset.Training, value => value.Expected == severity);
            Assert.Contains(SeverityDataset.Holdout, value => value.Expected == severity);
        }
    }

    [Fact]
    public void TrainingTheSameDatasetTwiceProducesTheSameModel()
    {
        var first = Predict(Train(), SeverityDataset.Holdout);
        var second = Predict(Train(), SeverityDataset.Holdout);

        // Reproducibility is what makes the reported figures a measurement rather than a sample. If
        // this fails, something unseeded has entered the trainer.
        Assert.Equal(
            first.Select(value => value.Predicted.Severity),
            second.Select(value => value.Predicted.Severity));

        Assert.All(
            first.Zip(second),
            pair => Assert.Equal(pair.First.Predicted.Confidence, pair.Second.Predicted.Confidence, 6));
    }

    [Fact]
    public async Task TheModelAndTheEnrichmentPathAreScoredOnTheSameHeldOutCases()
    {
        using var model = Train();
        var predictions = Predict(model, SeverityDataset.Holdout);

        var mlMatrix = new ConfusionMatrix();
        var aiMatrix = new ConfusionMatrix();
        var agreements = 0;
        var comparisons = 0;
        var rows = new List<ComparisonRow>(predictions.Count);

        // The same provider, resolved the same way, as the enrichment harness uses. Comparing a
        // model against a differently-configured baseline would not be a comparison.
        using var diagnostics = new PipelineDiagnostics(new EvaluationHost.MeterFactory());
        var providerOptions = EvaluationHost.ResolveProviderOptions();
        using var chatClient = EvaluationHost.BuildChatClient(providerOptions);

        var enrichment = new ChatClientEnrichmentService(
            chatClient,
            Options.Create(new EnrichmentOptions { Timeout = TimeSpan.FromSeconds(60) }),
            Options.Create(providerOptions),
            diagnostics,
            TimeProvider.System,
            NullLogger<ChatClientEnrichmentService>.Instance);

        foreach (var (labelled, predicted) in predictions)
        {
            mlMatrix.Record(Wire(labelled.Expected), Wire(predicted.Severity));

            var result = await enrichment.EnrichAsync(
                new EnrichmentRequest("evaluation", labelled.Title, labelled.Features.Text),
                CancellationToken.None);

            // A failed enrichment is recorded as a miss rather than skipped. Dropping it would score
            // the enrichment path only on the cases it happened to manage.
            var aiSeverity = result.Enrichment is { } enriched
                ? EnrichmentContract.ToWire(enriched.Severity)
                : "UNKNOWN";
            aiMatrix.Record(Wire(labelled.Expected), aiSeverity);

            comparisons++;

            if (string.Equals(aiSeverity, Wire(predicted.Severity), StringComparison.OrdinalIgnoreCase))
            {
                agreements++;
            }

            rows.Add(new ComparisonRow(labelled.Id, Wire(labelled.Expected), Wire(predicted.Severity), aiSeverity));
        }

        var report = BuildReport(model.Version, mlMatrix, aiMatrix, agreements, comparisons, rows);
        output.WriteLine(report);
        WriteReport(report);

        Assert.Equal(SeverityDataset.Holdout.Count, mlMatrix.Total);
        Assert.True(
            mlMatrix.Accuracy >= MinimumAccuracy,
            $"Severity model accuracy {mlMatrix.Accuracy:F2} fell below the regression floor {MinimumAccuracy:F2}.");
        Assert.True(
            mlMatrix.MacroF1() >= MinimumMacroF1,
            $"Severity model macro F1 {mlMatrix.MacroF1():F2} fell below the regression floor {MinimumMacroF1:F2}.");

        // The point of training a model at all is that it beats the deterministic classifier already
        // in the pipeline. If it stops doing that, the honest response is to remove it, and this is
        // what makes that visible rather than leaving a model in place because it is there.
        Assert.True(
            mlMatrix.Accuracy - aiMatrix.Accuracy >= MinimumMarginOverBaseline,
            $"Severity model accuracy {mlMatrix.Accuracy:F2} did not beat the enrichment baseline "
                + $"{aiMatrix.Accuracy:F2} by the required margin {MinimumMarginOverBaseline:F2}.");
    }

    [Fact]
    public async Task APredictionCarriesItsModelVersionAndEveryClassScore()
    {
        using var model = Train();

        var prediction = await model.PredictAsync(
            SeverityFeatures.From(
                "Explosion damages tanker hull",
                "An explosion tore a hole in the hull of a tanker. Two crew were injured and the vessel is taking on water.",
                EventType.MaritimeIncident,
                sourceCount: 3,
                sourceConfidence: 0.8,
                entityCount: 1,
                hasLocation: true),
            CancellationToken.None);

        Assert.NotNull(prediction);

        // Traceable to the exact model: trainer, feature-set version, and dataset version.
        Assert.Contains(SeverityTrainer.TrainerName, prediction.ModelVersion, StringComparison.Ordinal);
        Assert.Contains($"dataset-{SeverityDataset.Version}", prediction.ModelVersion, StringComparison.Ordinal);
        Assert.Equal(SeverityTrainer.Method, prediction.Method);

        // Every class, highest first, so a near-tie is visible instead of hidden behind the winner.
        Assert.Equal(4, prediction.Scores.Count);
        Assert.Equal(prediction.Severity, prediction.Scores[0].Severity);
        Assert.Equal(
            prediction.Scores.Select(value => value.Probability).OrderDescending(),
            prediction.Scores.Select(value => value.Probability));
        Assert.Equal(1.0, prediction.Scores.Sum(value => value.Probability), 2);
    }

    [Fact]
    public async Task ADisabledModelReturnsNothingRatherThanGuessing()
    {
        using var model = new MLNetSeverityModel(
            Options.Create(new SeverityModelOptions { Enabled = false }),
            NullLogger<MLNetSeverityModel>.Instance);

        Assert.False(model.IsReady);
        Assert.Equal("none", model.Version);

        // Null, not a default severity. A disabled model that returned Low would be indistinguishable
        // from a model that had assessed the report and found it unremarkable.
        Assert.Null(await model.PredictAsync(
            SeverityFeatures.From("t", "c", EventType.Other, 1, 0.5, 0, false),
            CancellationToken.None));
    }

    private static MLNetSeverityModel Train() => new(
        Options.Create(new SeverityModelOptions()),
        NullLogger<MLNetSeverityModel>.Instance);

    private static List<(LabelledSeverityCase Labelled, SeverityPrediction Predicted)> Predict(
        MLNetSeverityModel model,
        IReadOnlyList<LabelledSeverityCase> cases)
    {
        var results = new List<(LabelledSeverityCase, SeverityPrediction)>(cases.Count);

        foreach (var labelled in cases)
        {
            var prediction = model.PredictAsync(labelled.Features, CancellationToken.None).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The model returned no prediction for {labelled.Id}.");

            results.Add((labelled, prediction));
        }

        return results;
    }

    /// <summary>Wire form of a severity, matching the enrichment contract so the two are comparable.</summary>
    private static string Wire(Severity severity) => severity switch
    {
        Severity.Critical => "CRITICAL",
        Severity.High => "HIGH",
        Severity.Medium => "MEDIUM",
        Severity.Low => "LOW",
        _ => "UNKNOWN",
    };

    private static string BuildReport(
        string modelVersion,
        ConfusionMatrix ml,
        ConfusionMatrix ai,
        int agreements,
        int comparisons,
        IReadOnlyList<ComparisonRow> rows)
    {
        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        builder.AppendLine("# Severity model evaluation");
        builder.AppendLine();
        builder.AppendLine("<!-- Generated by Geopolitics.AiEvaluationTests. Do not edit by hand. -->");
        builder.AppendLine();
        builder.Append(culture, $"- Model: `{modelVersion}`").AppendLine();
        builder.Append(culture, $"- Trained on: {SeverityDataset.Training.Count} labelled cases").AppendLine();
        builder.Append(culture, $"- Evaluated on: {SeverityDataset.Holdout.Count} held-out cases, never trained on").AppendLine();
        builder.AppendLine("- Comparison baseline: the configured enrichment provider, run over the same held-out cases");
        builder.AppendLine();
        builder.AppendLine("> These figures measure how well a small linear model recovers this repository's");
        builder.AppendLine("> **author-assigned severity rubric** from 95 synthetic examples. That is a real");
        builder.AppendLine("> supervised-learning result and nothing more. It is not evidence that either system");
        builder.AppendLine("> can assess the severity of a real geopolitical event, and no figure below should be");
        builder.AppendLine("> quoted as if it were.");
        builder.AppendLine();

        builder.AppendLine("## Head to head");
        builder.AppendLine();
        builder.AppendLine("| Measure | Severity model | Enrichment provider |");
        builder.AppendLine("| --- | ---: | ---: |");
        builder.Append(culture, $"| Accuracy against labels | {ml.Accuracy:F2} | {ai.Accuracy:F2} |").AppendLine();
        builder.Append(culture, $"| Macro F1 against labels | {ml.MacroF1():F2} | {ai.MacroF1():F2} |").AppendLine();
        builder.Append(culture, $"| Correct of {ml.Total} | {ml.Correct} | {ai.Correct} |").AppendLine();
        builder.AppendLine();
        builder.Append(
            culture,
            $"The two systems agreed with each other on {agreements} of {comparisons} cases "
                + $"({(comparisons == 0 ? 0 : (double)agreements / comparisons):P0}).")
            .AppendLine();
        builder.AppendLine();
        builder.AppendLine("Agreement is not accuracy: both can agree and both be wrong, which is exactly why");
        builder.AppendLine("both are scored against the labels rather than against each other. The margin between");
        builder.AppendLine("the two accuracy figures is the only claim this evaluation makes for the model, and it");
        builder.AppendLine("is asserted — if the model stops beating the classifier already in the pipeline, the");
        builder.AppendLine("build fails, because at that point the honest thing to do is remove it.");
        builder.AppendLine();

        builder.AppendLine("## Severity model, per class");
        builder.AppendLine();
        builder.Append(ml.ToMarkdownTable());
        builder.AppendLine();

        builder.AppendLine("## Enrichment provider, per class");
        builder.AppendLine();
        builder.Append(ai.ToMarkdownTable());
        builder.AppendLine();

        builder.AppendLine("## Cases where the two disagreed");
        builder.AppendLine();

        var disagreements = rows
            .Where(row => !string.Equals(row.Model, row.Provider, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (disagreements.Length == 0)
        {
            builder.AppendLine("None.");
        }
        else
        {
            builder.AppendLine("| Case | Label | Severity model | Enrichment provider |");
            builder.AppendLine("| --- | --- | --- | --- |");

            foreach (var row in disagreements)
            {
                builder
                    .Append(culture, $"| `{row.Id}` | `{row.Expected}` | `{row.Model}` | `{row.Provider}` |")
                    .AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Reading these numbers");
        builder.AppendLine();
        builder.AppendLine(SeverityDataset.Notice);
        builder.AppendLine();
        builder.AppendLine("Forty-five held-out cases is far too few to quote a confidence interval for. What the");
        builder.AppendLine("set is good for is detecting a regression: if a change to the features, the trainer, or");
        builder.AppendLine("the corpus makes these scores drop, something got worse. Treat any single figure here");
        builder.AppendLine("as a smoke test.");
        builder.AppendLine();
        builder.AppendLine("The model is a **second opinion** in the pipeline and never sets the severity an");
        builder.AppendLine("incident is stored or displayed with. A model trained on a small synthetic corpus has");
        builder.AppendLine("no business overruling a source that declared its own.");

        return builder.ToString();
    }

    private static void WriteReport(string report)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "data", "severity-model");

            if (Directory.Exists(candidate))
            {
                File.WriteAllText(Path.Combine(candidate, "RESULTS.md"), report);
                return;
            }

            directory = directory.Parent;
        }
    }

    private sealed record ComparisonRow(string Id, string Expected, string Model, string Provider);
}
