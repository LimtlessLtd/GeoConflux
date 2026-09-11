using System.Globalization;
using System.Text;

namespace Geopolitics.AiEvaluationTests;

/// <param name="Label">The class these figures describe.</param>
/// <param name="Support">How many labelled cases actually belong to this class.</param>
/// <param name="TruePositives">Predicted as this class and correct.</param>
/// <param name="FalsePositives">Predicted as this class and wrong.</param>
/// <param name="FalseNegatives">Belongs to this class but was predicted as something else.</param>
public sealed record ClassMetrics(string Label, int Support, int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => Ratio(TruePositives, TruePositives + FalsePositives);

    public double Recall => Ratio(TruePositives, TruePositives + FalseNegatives);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
}

/// <summary>
/// Per-class and aggregate scores for one multi-class task.
/// <para>
/// Macro-averaged rather than micro-averaged, deliberately. The fixture set is small and its classes
/// are uneven, so a micro average would mostly report how well the common classes did. The macro
/// average weights every class equally and therefore exposes a category the system never gets right,
/// which is the failure this harness exists to catch.
/// </para>
/// </summary>
public sealed class ConfusionMatrix
{
    private readonly Dictionary<string, Dictionary<string, int>> counts = new(StringComparer.Ordinal);
    private readonly SortedSet<string> labels = new(StringComparer.Ordinal);

    public int Total { get; private set; }

    public int Correct { get; private set; }

    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;

    public void Record(string expected, string actual)
    {
        labels.Add(expected);
        labels.Add(actual);

        if (!counts.TryGetValue(expected, out var row))
        {
            row = new Dictionary<string, int>(StringComparer.Ordinal);
            counts[expected] = row;
        }

        row[actual] = row.GetValueOrDefault(actual) + 1;
        Total++;

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Correct++;
        }
    }

    /// <summary>Scores for every class that appears as an expected label or as a prediction.</summary>
    public IReadOnlyList<ClassMetrics> PerClass() =>
    [
        .. labels.Select(label =>
        {
            var truePositives = counts.TryGetValue(label, out var row) ? row.GetValueOrDefault(label) : 0;
            var support = counts.TryGetValue(label, out var expectedRow) ? expectedRow.Values.Sum() : 0;
            var predicted = counts.Values.Sum(other => other.GetValueOrDefault(label));

            return new ClassMetrics(label, support, truePositives, predicted - truePositives, support - truePositives);
        }),
    ];

    /// <summary>
    /// Macro F1 over classes with at least one labelled example. Classes the system invented but
    /// that no fixture uses are excluded from the average: they are false positives, already
    /// penalised in the precision of the class they were taken from, and averaging in their zero
    /// would punish the same mistake twice.
    /// </summary>
    public double MacroF1()
    {
        var scored = PerClass().Where(metrics => metrics.Support > 0).ToArray();
        return scored.Length == 0 ? 0 : scored.Average(metrics => metrics.F1);
    }

    public string ToMarkdownTable()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Class | Support | Precision | Recall | F1 |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: |");

        foreach (var metrics in PerClass().Where(metrics => metrics.Support > 0 || metrics.FalsePositives > 0))
        {
            builder.Append(CultureInfo.InvariantCulture, $"| `{metrics.Label}` | {metrics.Support} ");
            builder.Append(CultureInfo.InvariantCulture, $"| {metrics.Precision:F2} | {metrics.Recall:F2} | {metrics.F1:F2} |");
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

/// <param name="TruePositives">Correctly reported items.</param>
/// <param name="FalsePositives">Reported but not expected.</param>
/// <param name="FalseNegatives">Expected but not reported.</param>
public sealed record SetMetrics(int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => Ratio(TruePositives, TruePositives + FalsePositives);

    public double Recall => Ratio(TruePositives, TruePositives + FalseNegatives);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
}

/// <summary>
/// Accumulates precision and recall for tasks whose answer is a set rather than one label, such as
/// location naming and entity extraction. An expected empty set contributes nothing to recall, so a
/// fixture that names no place cannot inflate the score by being trivially satisfied.
/// </summary>
public sealed class SetScorer
{
    private int truePositives;
    private int falsePositives;
    private int falseNegatives;

    public void Record(IReadOnlyCollection<string> expected, IReadOnlyCollection<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSet = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);

        truePositives += actualSet.Count(expectedSet.Contains);
        falsePositives += actualSet.Count(value => !expectedSet.Contains(value));
        falseNegatives += expectedSet.Count(value => !actualSet.Contains(value));
    }

    public SetMetrics Result => new(truePositives, falsePositives, falseNegatives);
}
