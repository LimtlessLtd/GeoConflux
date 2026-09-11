using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Transforms;

namespace Geopolitics.Infrastructure.Ml;

/// <param name="Transformer">The fitted pipeline.</param>
/// <param name="Classes">Severity classes in the order the model emits scores, read from the trained schema rather than assumed.</param>
/// <param name="Version">Trainer, feature-set version, and dataset version, recorded with every prediction.</param>
public sealed record TrainedSeverityModel(ITransformer Transformer, IReadOnlyList<Severity> Classes, string Version);

/// <summary>
/// Builds and fits the severity model.
/// <para>
/// The model is trained from the embedded corpus rather than loaded from a committed binary. That is
/// a deliberate trade: training costs a few hundred milliseconds once per process, and in exchange
/// every input to the model is a readable file under version control. A committed <c>.zip</c> would
/// be an opaque artefact that nobody could diff, reproduce, or verify came from the dataset beside
/// it — and the version it claimed would be whatever the last person to regenerate it typed.
/// </para>
/// <para>
/// Everything here is pinned for determinism: a fixed seed, a single training thread, and a key
/// ordering taken from the label values rather than from the order rows happen to arrive in. Two runs
/// of the same dataset produce the same model, which is what makes the evaluation figures a
/// measurement rather than a sample.
/// </para>
/// </summary>
public static class SeverityTrainer
{
    /// <summary>
    /// Version of the feature set, not of the code. Bump it when a feature is added, removed, or
    /// redefined, so a stored prediction cannot be silently attributed to a model that saw something
    /// different.
    /// </summary>
    public const int FeatureVersion = 1;

    public const string TrainerName = "sdca-maximum-entropy";

    /// <summary>Wire-format method tag, matching the <c>keyword</c> and <c>ai:</c> convention used elsewhere.</summary>
    public const string Method = $"ml:{TrainerName}";

    /// <summary>
    /// Fixed so the trainer is reproducible. ML.NET seeds its own randomness from this, and without
    /// it two runs over identical data would disagree in the third decimal place of every metric.
    /// </summary>
    private const int Seed = 1;

    public static MLContext CreateContext() => new(Seed);

    public static TrainedSeverityModel Train(MLContext context, IReadOnlyList<LabelledSeverityCase> cases)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cases);

        if (cases.Count == 0)
        {
            throw new InvalidOperationException("The severity model cannot be trained on an empty dataset.");
        }

        var data = context.Data.LoadFromEnumerable(cases.Select(value => SeverityRow.From(value.Features, value.Expected)));
        var transformer = BuildPipeline(context).Fit(data);

        return new TrainedSeverityModel(
            transformer,
            ReadClassOrder(transformer.Transform(data).Schema),
            $"{TrainerName}/features-{FeatureVersion}/dataset-{SeverityDataset.Version}");
    }

    private static EstimatorChain<KeyToValueMappingTransformer> BuildPipeline(MLContext context) =>
        context.Transforms.Conversion.MapValueToKey(
                outputColumnName: "Label",
                inputColumnName: nameof(SeverityRow.Label),

                // By value, not by first appearance. Ordering keys by the order rows arrive in would
                // make the class indices depend on how the dataset file happens to be sorted, so
                // reordering two lines in the corpus would renumber every class.
                keyOrdinality: ValueToKeyMappingEstimator.KeyOrdinality.ByValue)

            // Bag-of-words plus character and word n-grams, which is what FeaturizeText assembles.
            // The corpus is 95 training rows; anything with more capacity than this would memorise it.
            .Append(context.Transforms.Text.FeaturizeText("TextFeatures", nameof(SeverityRow.Text)))
            .Append(context.Transforms.Categorical.OneHotEncoding("EventTypeFeatures", nameof(SeverityRow.EventType)))
            .Append(context.Transforms.Concatenate(
                "Features",
                "TextFeatures",
                "EventTypeFeatures",
                nameof(SeverityRow.SourceCount),
                nameof(SeverityRow.SourceConfidence),
                nameof(SeverityRow.EntityCount),
                nameof(SeverityRow.HasLocation)))

            // Without this the numeric features sit on wildly different scales to the text vector,
            // and a linear model reads "five sources" as an enormous signal purely because 5 is a
            // large number next to a normalised term weight.
            .Append(context.Transforms.NormalizeMinMax("Features"))
            .Append(context.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                new SdcaMaximumEntropyMulticlassTrainer.Options
                {
                    LabelColumnName = "Label",
                    FeatureColumnName = "Features",

                    // One thread, so the order of updates is fixed and the fit is reproducible.
                    NumberOfThreads = 1,

                    // Firmer than the default. With far more features than rows, a linear model will
                    // happily drive training error to zero by leaning on individual words; this is
                    // what keeps it fitting the rubric rather than the vocabulary.
                    L2Regularization = 0.05f,
                    MaximumNumberOfIterations = 200,
                }))
            .Append(context.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

    /// <summary>
    /// Reads the class order out of the trained schema rather than assuming it.
    /// <para>
    /// The score vector is positional, and nothing in the type system connects position 2 to "High".
    /// Assuming alphabetical order, or enum order, would produce a model that is confidently wrong in
    /// a way no test of overall accuracy would catch — every prediction would be mislabelled
    /// consistently.
    /// </para>
    /// </summary>
    private static List<Severity> ReadClassOrder(DataViewSchema schema)
    {
        VBuffer<ReadOnlyMemory<char>> names = default;
        schema["Score"].Annotations.GetValue("SlotNames", ref names);

        var classes = new List<Severity>(names.Length);

        foreach (var name in names.DenseValues())
        {
            classes.Add(Enum.Parse<Severity>(name.ToString(), ignoreCase: true));
        }

        return classes.Count == 0
            ? throw new InvalidOperationException("The trained severity model exposed no class names.")
            : classes;
    }
}

/// <summary>
/// The flat row shape ML.NET loads. Mutable with public setters because that is what the framework
/// requires of a data class; it is internal and never escapes this assembly.
/// </summary>
internal sealed class SeverityRow
{
    public string Text { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public float SourceCount { get; set; }

    public float SourceConfidence { get; set; }

    public float EntityCount { get; set; }

    public float HasLocation { get; set; }

    public string Label { get; set; } = string.Empty;

    public static SeverityRow From(SeverityFeatures features, Severity? label = null) => new()
    {
        Text = features.Text,
        EventType = features.EventType.ToString(),
        SourceCount = features.SourceCount,
        SourceConfidence = (float)features.SourceConfidence,
        EntityCount = features.EntityCount,
        HasLocation = features.HasLocation ? 1f : 0f,

        // Prediction rows carry a placeholder the transform maps but never reads. It must still be a
        // label the key mapping knows, or the row fails to transform.
        Label = (label ?? Severity.Low).ToString(),
    };
}

/// <summary>Prediction shape. Same framework constraints as <see cref="SeverityRow"/>.</summary>
internal sealed class SeverityRowPrediction
{
    [ColumnName("PredictedLabel")]
    public string PredictedLabel { get; set; } = string.Empty;

#pragma warning disable CA1819 // ML.NET populates this by assignment; a read-only view is not an option.
    [ColumnName("Score")]
    public float[] Score { get; set; } = [];
#pragma warning restore CA1819
}
