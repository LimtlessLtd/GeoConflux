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

    /// <summary>
    /// The four breakdowns the plan asks for: by region, language, tier and platform.
    /// <para>
    /// Returned together from one call because they are one statement. A reader comparing the tier
    /// split against the language split is doing the thing this is for — seeing that the picture is
    /// one platform, or one language, or one country — and assembling it from four round trips
    /// invites three of them to be shown while the fourth quietly fails.
    /// </para>
    /// </summary>
    Task<BreadthTotals> CountBreadthAsync(CancellationToken cancellationToken);
}

/// <param name="ByRegion">
/// Placed observations per country code. Country rather than anything finer, because it is what the
/// resolver actually establishes for every placed record: a theatre needs bounds and a continent
/// needs a table, and both would be a second classification layered on top of the one that was
/// measured. The per-theatre section above is where finer detail lives, for the three theatres where
/// this project has done the work to support it.
/// </param>
/// <param name="ByLanguage">
/// Observations per BCP-47 tag. Counts what sources stated and what enrichment detected together,
/// because a reader asking "what languages is this reading" wants the answer, not the provenance of
/// the answer. Unknown is its own row rather than being dropped.
/// </param>
/// <param name="ByTier">Published reporting against user-generated claims.</param>
/// <param name="ByPlatform">Claims per open platform. Empty until Tier B has been collected.</param>
public sealed record BreadthTotals(
    IReadOnlyList<CategoryCount> ByRegion,
    IReadOnlyList<CategoryCount> ByLanguage,
    IReadOnlyList<CategoryCount> ByTier,
    IReadOnlyList<CategoryCount> ByPlatform);
