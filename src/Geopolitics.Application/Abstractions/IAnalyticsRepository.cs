using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <param name="Category">Wire-format name of the class counted, such as <c>High</c> or <c>PIRACY</c>.</param>
/// <param name="Count">How many records fell into it.</param>
public sealed record CategoryCount(string Category, int Count);

/// <param name="CountryCode">ISO country code the gazetteer resolved, or <c>null</c> for incidents placed outside any country — open ocean, mostly.</param>
/// <param name="Name">A representative resolved place name, so a reader sees "Red Sea" rather than a bare code.</param>
public sealed record RegionCount(string? CountryCode, string Name, int Count);

/// <summary>
/// The four scalars an incident contributes to the activity score, and nothing else.
/// <para>
/// A deliberately narrow projection. The score is computed over every incident in the window, so
/// pulling whole aggregates — titles, summaries, evidence lists, entity arrays — would move
/// kilobytes per row to produce one number.
/// </para>
/// </summary>
public sealed record IncidentScoreInput(
    Severity Severity,
    DateTimeOffset OccurredAt,
    int ObservationCount,
    double ClassificationConfidence);

/// <param name="Inputs">The projected incidents, newest first.</param>
/// <param name="Truncated">
/// Whether the window held more incidents than the cap allowed. Reported rather than swallowed: a
/// score computed over part of a window is a different number, and a reader is entitled to know.
/// </param>
public sealed record IncidentScoreSample(IReadOnlyList<IncidentScoreInput> Inputs, bool Truncated);

/// <summary>
/// Aggregate read queries behind the analytics views.
/// <para>
/// Separate from <see cref="IIncidentRepository"/> because it answers a different kind of question.
/// That interface loads aggregates to be read and mutated; this one returns counts and projections
/// that no caller can write back. Keeping them apart stops analytics work from acquiring the ability
/// to change state, and lets the implementation push <c>GROUP BY</c> into the database rather than
/// materialising rows the caller would only tally.
/// </para>
/// </summary>
public interface IAnalyticsRepository
{
    /// <summary>Incidents whose <c>OccurredAt</c> falls in the half-open interval <c>[windowStart, windowEnd)</c>.</summary>
    Task<int> CountIncidentsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>Incidents in the window that have more than one piece of linked evidence.</summary>
    Task<int> CountCorrelatedIncidentsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryCount>> CountIncidentsBySeverityAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryCount>> CountIncidentsByEventTypeAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    /// <summary>Located incidents grouped by resolved country, busiest first.</summary>
    Task<IReadOnlyList<RegionCount>> CountIncidentsByRegionAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Observations received in the window, grouped by the kind of source that produced them.</summary>
    Task<IReadOnlyList<CategoryCount>> CountObservationsByKindAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    /// <summary>Observations received in the window, grouped by processing status.</summary>
    Task<IReadOnlyList<CategoryCount>> CountObservationsByStatusAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// Minimal per-incident projection for the timeseries and the activity score, newest first and
    /// capped at <paramref name="take"/>.
    /// </summary>
    Task<IncidentScoreSample> SampleIncidentsAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken);
}
