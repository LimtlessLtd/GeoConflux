using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// Everything the severity model is allowed to see about one report.
/// <para>
/// Deliberately narrow, and deliberately in the Application layer rather than inside the model
/// implementation. Making the feature set an explicit contract means a change to it is a change to a
/// signature that the trainer, the evaluator, and the pipeline all compile against — rather than a
/// silent drift between what was trained on and what is predicted from, which is the classic way a
/// model quietly stops meaning anything.
/// </para>
/// <para>
/// The four numeric features exist because a bag of words cannot see them. One unconfirmed report and
/// four corroborating ones can use identical wording and still differ in what they justify
/// concluding.
/// </para>
/// </summary>
/// <param name="Text">Headline and body, as the pipeline normalised them.</param>
/// <param name="EventType">The category already assigned, which the model treats as a categorical feature rather than re-deriving.</param>
/// <param name="SourceCount">How many observations support this, at least 1.</param>
/// <param name="SourceConfidence">Confidence of the classification that produced the category, 0-1.</param>
/// <param name="EntityCount">How many actors were extracted.</param>
/// <param name="HasLocation">Whether the deterministic resolver could place it.</param>
public sealed record SeverityFeatures(
    string Text,
    EventType EventType,
    int SourceCount,
    double SourceConfidence,
    int EntityCount,
    bool HasLocation)
{
    /// <summary>
    /// Builds features from the parts the pipeline already holds, applying the same normalisation the
    /// trainer applied. Callers must not assemble <see cref="SeverityFeatures"/> by hand for
    /// prediction: doing so is how training and inference come to disagree about what a feature means.
    /// </summary>
    public static SeverityFeatures From(
        string? title,
        string? content,
        EventType eventType,
        int sourceCount,
        double sourceConfidence,
        int entityCount,
        bool hasLocation) =>
        new(
            string.Join(' ', new[] { title, content }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
            eventType,
            Math.Max(1, sourceCount),
            Math.Clamp(sourceConfidence, 0, 1),
            Math.Max(0, entityCount),
            hasLocation);
}

/// <param name="Severity">The class this label describes.</param>
/// <param name="Probability">The model's probability for it, 0-1.</param>
public sealed record SeverityScore(Severity Severity, double Probability);

/// <param name="Severity">Highest-scoring class.</param>
/// <param name="Confidence">Its probability. Reported as a model score and labelled as one — it is a calibration of this model against its training distribution, not a measure of how likely the event is to be severe.</param>
/// <param name="Scores">Every class with its probability, so a near-tie is visible rather than hidden behind the winner.</param>
/// <param name="ModelVersion">Identifies the exact model that produced this, for reproducibility.</param>
/// <param name="Method">Wire-format method tag, for example <c>ml:sdca-maximum-entropy</c>, matching the convention used by the keyword and AI classifiers.</param>
public sealed record SeverityPrediction(
    Severity Severity,
    double Confidence,
    IReadOnlyList<SeverityScore> Scores,
    string ModelVersion,
    string Method);

/// <summary>
/// A conventional, trained severity classifier, kept separate from the language model path.
/// <para>
/// It exists to be evaluated. A language model's severity assessment is difficult to attribute, hard
/// to reproduce, and impossible to compare against a baseline that shares none of its machinery; a
/// small supervised model trained on a fixed labelled corpus can be scored against held-out labels
/// and against the language model on the same cases (ADR 009).
/// </para>
/// <para>
/// It is a <b>second opinion</b>, never the authority. The pipeline records what it predicted and
/// whether that agreed with the classification already in force; it does not let the prediction
/// replace one. A model trained on a small synthetic corpus has no business overruling a source that
/// declared its own severity.
/// </para>
/// </summary>
public interface ISeverityModel
{
    /// <summary>
    /// Identifies the trained model: trainer, feature-set version, and dataset version. Persisted with
    /// every prediction so a stored result can be traced to what produced it.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Whether the model can serve predictions. False when training failed or the model is disabled,
    /// in which case the pipeline continues without a second opinion rather than failing.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Predicts a severity class, or <see langword="null"/> when the model is unavailable. Null is a
    /// normal outcome, not an error: a missing second opinion must never fail the processing of an
    /// observation.
    /// </summary>
    Task<SeverityPrediction?> PredictAsync(SeverityFeatures features, CancellationToken cancellationToken);
}
