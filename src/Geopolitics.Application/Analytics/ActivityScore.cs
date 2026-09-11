using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.Application.Analytics;

/// <param name="Label">The factor's name as the dashboard shows it.</param>
/// <param name="Description">One line on what the factor does to the total.</param>
/// <param name="Value">The factor's aggregate effect over the window, normally the mean multiplier applied.</param>
public sealed record ScoreComponent(string Label, string Description, double Value);

/// <param name="Value">The heuristic score, 0-100.</param>
/// <param name="Band">A coarse label for the value: <c>quiet</c>, <c>moderate</c>, <c>elevated</c>, or <c>high</c>.</param>
/// <param name="WeightedTotal">Sum of every incident's weighted contribution, before rate-normalisation.</param>
/// <param name="WeightedPerDay">That total divided by the window length, which is the quantity the score is a function of.</param>
/// <param name="IncidentsScored">How many incidents went into it.</param>
/// <param name="Truncated">Whether the window held more incidents than the sample cap, making this a partial figure.</param>
/// <param name="Components">Aggregate effect of each factor, so the number can be taken apart.</param>
/// <param name="Formula">The calculation in one line, carried with the result rather than living only in documentation.</param>
/// <param name="Notice">Plain-language statement of what this score is not.</param>
public sealed record ActivityScore(
    double Value,
    string Band,
    double WeightedTotal,
    double WeightedPerDay,
    int IncidentsScored,
    bool Truncated,
    IReadOnlyList<ScoreComponent> Components,
    string Formula,
    string Notice);

/// <summary>
/// The Geopolitical Activity Score: a bounded, documented heuristic summarising how much weighted
/// activity a window contains.
/// <para>
/// <b>It summarises this database, not the world.</b> It says how much material the system ingested,
/// how severe that material claimed to be, how recent it was, how well corroborated, and how
/// confidently classified. It cannot say whether a region is dangerous, and a quiet score may mean
/// only that little was reported, that no adapter was configured, or that a feed was down.
/// </para>
/// <para>
/// Every incident contributes <c>severity x recency x corroboration x confidence</c>. Those four
/// multipliers are four of the five components the specification lists; the fifth, event frequency,
/// is the division by window length that turns a total into a rate. Each is deliberately simple and
/// separately reported, because a score a reader cannot take apart is a score they must take on
/// trust.
/// </para>
/// </summary>
public static class ActivityScoreCalculator
{
    /// <summary>
    /// How much of the window an incident's weight takes to halve: a quarter of it, so the oldest
    /// incident still inside the window retains one sixteenth of its weight. Tying the half-life to
    /// the window rather than fixing it in hours is what makes "recent" mean the same thing at every
    /// zoom level.
    /// </summary>
    private const double HalfLifeFraction = 0.25;

    /// <summary>
    /// Added per doubling of linked evidence: two independent reports of one event make it half
    /// again as significant, four make it twice. Logarithmic rather than linear because the second
    /// source is worth far more than the tenth, and because one wire story syndicated forty times
    /// must not outweigh a genuinely corroborated event.
    /// </summary>
    private const double CorroborationPerDoubling = 0.5;

    /// <summary>Ceiling on that bonus, so no single incident can dominate a window on volume of coverage alone.</summary>
    private const double MaxCorroboration = 3.0;

    /// <summary>
    /// Floor on the confidence multiplier. A classification nobody is sure of still describes an
    /// event that was reported, so confidence scales a contribution between this floor and 1 rather
    /// than between 0 and 1.
    /// </summary>
    private const double MinConfidenceWeight = 0.4;

    /// <summary>
    /// Weighted incidents per day at which the score reaches about 63.
    /// <para>
    /// The one frankly arbitrary constant here, and the reason <see cref="ActivityScore.WeightedPerDay"/>
    /// is reported beside the score: the rate is the measurement, and the 0-100 value is a
    /// presentation of it.
    /// </para>
    /// <para>
    /// It was 6, chosen against the recorded replay stream, and that turned out to be the wrong scale
    /// the moment the pipeline was pointed at live news: four public feeds produce roughly 34 weighted
    /// incidents a day, which pinned the score at 99.7 and made the number useless — every day would
    /// have looked maximally busy. Re-set to 30 so real ingestion lands mid-range and has somewhere
    /// to move in both directions. This is exactly the recalibration ADR 018 said a different volume
    /// would force, and it is why scores from two differently-calibrated deployments cannot be
    /// compared.
    /// </para>
    /// </summary>
    public const double SaturationRatePerDay = 30.0;

    public const string Formula =
        "score = 100 x (1 - exp(-R / 30)); R = (1 / days) x SUM[ severity x 0.5^(age / (window / 4)) "
        + "x min(3, 1 + 0.5 x log2(evidence)) x (0.4 + 0.6 x confidence) ]";

    public const string Notice =
        "A heuristic summary of what this system ingested during the window. It is not an objective "
        + "measure of geopolitical risk, danger, or escalation, and a low score may mean only that "
        + "little was reported or that a source was unavailable.";

    /// <summary>
    /// Relative weight per severity, doubling at each step. The claim this encodes is statable and
    /// arguable — one Critical incident counts as eight Low ones — which is the point of writing it
    /// down. Unknown is not zero, because an unclassified report is still a report.
    /// </summary>
    public static double SeverityWeight(Severity severity) => severity switch
    {
        Severity.Critical => 8,
        Severity.High => 4,
        Severity.Medium => 2,
        Severity.Low => 1,
        _ => 0.5,
    };

    public static ActivityScore Calculate(IncidentScoreSample sample, AnalyticsWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(window);

        var halfLife = window.Duration * HalfLifeFraction;
        var total = 0.0;
        var severityTotal = 0.0;
        var recencyTotal = 0.0;
        var corroborationTotal = 0.0;
        var confidenceTotal = 0.0;

        foreach (var incident in sample.Inputs)
        {
            var severity = SeverityWeight(incident.Severity);

            // Clamped at zero age: an incident timestamped slightly ahead of now — a source clock a
            // few minutes fast — should count as current, not be rewarded with a weight above 1.
            var age = now - incident.OccurredAt;
            var recency = Math.Pow(0.5, Math.Max(0, age / halfLife));

            var evidence = Math.Max(1, incident.ObservationCount);
            var corroboration = Math.Min(MaxCorroboration, 1 + (CorroborationPerDoubling * Math.Log2(evidence)));

            var confidence = MinConfidenceWeight
                + ((1 - MinConfidenceWeight) * Math.Clamp(incident.ClassificationConfidence, 0, 1));

            total += severity * recency * corroboration * confidence;
            severityTotal += severity;
            recencyTotal += recency;
            corroborationTotal += corroboration;
            confidenceTotal += confidence;
        }

        var count = sample.Inputs.Count;
        var perDay = total / window.Duration.TotalDays;
        var value = 100 * (1 - Math.Exp(-perDay / SaturationRatePerDay));

        return new ActivityScore(
            Math.Round(value, 1),
            Band(value),
            Math.Round(total, 2),
            Math.Round(perDay, 2),
            count,
            sample.Truncated,
            [
                new ScoreComponent(
                    "Severity",
                    "Mean severity weight, where Low counts 1 and Critical counts 8.",
                    Mean(severityTotal, count)),
                new ScoreComponent(
                    "Recency",
                    $"Mean share of weight retained; it halves every {Describe(halfLife)}.",
                    Mean(recencyTotal, count)),
                new ScoreComponent(
                    "Corroboration",
                    "Mean multiplier from linked evidence; 1.0 means single-source throughout.",
                    Mean(corroborationTotal, count)),
                new ScoreComponent(
                    "Confidence",
                    "Mean multiplier from classification confidence, floored at 0.4.",
                    Mean(confidenceTotal, count)),
                new ScoreComponent(
                    "Frequency",
                    "Weighted incidents per day, the rate the score is a function of.",
                    Math.Round(perDay, 2)),
            ],
            Formula,
            Notice);
    }

    private static double Mean(double total, int count) => count == 0 ? 0 : Math.Round(total / count, 2);

    /// <summary>
    /// Coarse band for the value. A word as well as a number, because a reader comparing 41 with 46
    /// will otherwise read a difference into them that the heuristic cannot support.
    /// </summary>
    private static string Band(double value) => value switch
    {
        < 15 => "quiet",
        < 40 => "moderate",
        < 70 => "elevated",
        _ => "high",
    };

    private static string Describe(TimeSpan span) => span.TotalHours < 48
        ? $"{span.TotalHours:0.#} hours"
        : $"{span.TotalDays:0.#} days";
}
