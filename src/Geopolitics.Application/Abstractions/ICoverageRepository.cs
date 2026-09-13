using Geopolitics.Application.Coverage;

namespace Geopolitics.Application.Abstractions;

/// <param name="Placed">Observations in this theatre that the resolver managed to put on the map.</param>
/// <param name="ByPrecision">How precisely they were placed, keyed by the precision's name.</param>
/// <param name="BySource">Which sources they came from, keyed by source name.</param>
public sealed record TheatreTotals(
    int Placed,
    IReadOnlyList<CategoryCount> ByPrecision,
    IReadOnlyList<CategoryCount> BySource);

/// <summary>
/// The counts behind the per-theatre coverage statement.
/// <para>
/// Separate from <see cref="IAnalyticsRepository"/> because it answers a different question. Analytics
/// asks what happened; this asks what the system is in a position to know, which is the question a
/// reader needs answered before they can take the first set of numbers seriously.
/// </para>
/// </summary>
public interface ICoverageRepository
{
    /// <summary>
    /// Counts placed observations in one theatre, broken down by precision and by source.
    /// </summary>
    Task<TheatreTotals> CountPlacedAsync(Theatre theatre, CancellationToken cancellationToken);

    /// <summary>
    /// Observations that named a place the gazetteer could not resolve.
    /// <para>
    /// Deliberately not per theatre, because it cannot be: an observation with no coordinate cannot
    /// be attributed to a region. Some of these belong to these three theatres and there is no honest
    /// way to say how many, which is itself worth reporting.
    /// </para>
    /// </summary>
    Task<int> CountUnplacedAsync(CancellationToken cancellationToken);
}
