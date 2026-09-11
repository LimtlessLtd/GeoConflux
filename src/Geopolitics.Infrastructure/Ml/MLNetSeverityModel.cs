using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.ML;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Ml;

/// <summary>Whether the trained severity model runs at all.</summary>
public sealed class SeverityModelOptions
{
    public const string SectionName = "SeverityModel";

    /// <summary>
    /// Turning this off removes the second opinion and nothing else. The pipeline records no ML
    /// prediction and continues; no classification changes, because the model was never allowed to
    /// set one.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The trained severity classifier, served from an in-process ML.NET model.
/// <para>
/// Training happens once, lazily, on first use. The cost lands on the background processor rather
/// than on a request thread, because that is who asks first in every host that runs the pipeline. A
/// host that only serves the prediction endpoint pays it on its first call instead, which is
/// acceptable for a capability that is explicitly a second opinion.
/// </para>
/// <para>
/// A training failure disables the model rather than failing the host. The model exists to add a
/// comparison, and a platform that will not start because a supplementary classifier could not be
/// fitted has the dependency backwards.
/// </para>
/// </summary>
public sealed partial class MLNetSeverityModel : ISeverityModel, IDisposable
{
    /// <summary>
    /// ML.NET prediction engines are explicitly not thread-safe, and the processor runs several
    /// workers. A lock rather than a pool because a prediction is a sub-millisecond matrix multiply
    /// against a small linear model: contention here is cheaper than the machinery to avoid it, and
    /// far easier to reason about than a pool whose lifetime has to be managed.
    /// </summary>
    private readonly Lock gate = new();

    private readonly Lazy<Loaded?> model;
    private readonly ILogger<MLNetSeverityModel> logger;

    public MLNetSeverityModel(IOptions<SeverityModelOptions> options, ILogger<MLNetSeverityModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.logger = logger;

        var enabled = options.Value.Enabled;
        model = new Lazy<Loaded?>(
            () => enabled ? TryTrain() : null,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Version => model.Value?.Trained.Version ?? "none";

    public bool IsReady => model.Value is not null;

    public Task<SeverityPrediction?> PredictAsync(SeverityFeatures features, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(features);
        cancellationToken.ThrowIfCancellationRequested();

        if (model.Value is not { } loaded)
        {
            return Task.FromResult<SeverityPrediction?>(null);
        }

        SeverityRowPrediction raw;

        lock (gate)
        {
            raw = loaded.Engine.Predict(SeverityRow.From(features));
        }

        return Task.FromResult<SeverityPrediction?>(Describe(raw, loaded.Trained));
    }

    private static SeverityPrediction Describe(SeverityRowPrediction raw, TrainedSeverityModel trained)
    {
        var scores = new List<SeverityScore>(trained.Classes.Count);

        for (var index = 0; index < trained.Classes.Count && index < raw.Score.Length; index++)
        {
            scores.Add(new SeverityScore(trained.Classes[index], Math.Round(raw.Score[index], 4)));
        }

        // Ordered by probability so a near-tie is visible in the payload rather than hidden behind
        // the winning label.
        scores.Sort((left, right) => right.Probability.CompareTo(left.Probability));

        var predicted = Enum.TryParse<Severity>(raw.PredictedLabel, ignoreCase: true, out var parsed)
            ? parsed
            : Severity.Unknown;

        return new SeverityPrediction(
            predicted,
            scores.Count == 0 ? 0 : scores[0].Probability,
            scores,
            trained.Version,
            SeverityTrainer.Method);
    }

    private Loaded? TryTrain()
    {
        try
        {
            var context = SeverityTrainer.CreateContext();
            var trained = SeverityTrainer.Train(context, SeverityDataset.Training);
            var engine = context.Model.CreatePredictionEngine<SeverityRow, SeverityRowPrediction>(trained.Transformer);

            LogTrained(logger, trained.Version, SeverityDataset.Training.Count);
            return new Loaded(trained, engine);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            // Broad on purpose, and narrow enough to exclude the failures that should crash. A model
            // that cannot be fitted is a missing second opinion, not a broken platform.
            LogTrainingFailed(logger, exception);
            return null;
        }
    }

    public void Dispose()
    {
        if (model.IsValueCreated)
        {
            model.Value?.Engine.Dispose();
        }
    }

    private sealed record Loaded(TrainedSeverityModel Trained, PredictionEngine<SeverityRow, SeverityRowPrediction> Engine);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Severity model {Version} trained on {CaseCount} labelled case(s).")]
    private static partial void LogTrained(ILogger logger, string version, int caseCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The severity model could not be trained; predictions will be unavailable.")]
    private static partial void LogTrainingFailed(ILogger logger, Exception exception);
}
