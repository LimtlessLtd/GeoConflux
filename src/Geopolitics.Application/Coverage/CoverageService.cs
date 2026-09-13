using Geopolitics.Application.Abstractions;

namespace Geopolitics.Application.Coverage;

/// <summary>Builds the per-theatre statement of what this system has placed and how precisely.</summary>
public interface ICoverageService
{
    Task<CoverageReport> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>
/// States, per theatre, what has been placed and at what precision.
/// <para>
/// This exists because a map is silent about its own gaps. A reader looking at three dots over Tigray
/// has no way to tell whether that is three things happening or three things reported, and the
/// difference is the whole of what this project is trying to be careful about. Counting what was
/// placed, naming the sources it came from, and stating the ceiling the lexicon imposes turns an
/// empty region from an implied claim into a stated one.
/// </para>
/// </summary>
public sealed class CoverageService(
    ICoverageRepository repository,
    IPlaceLexicon lexicon,
    ICollectionCoverage collection) : ICoverageService
{
    /// <summary>
    /// What the breadth figures establish, stated next to them rather than left to be assumed.
    /// <para>
    /// A count of what arrived is not a count of what happened, and the gap between those two is the
    /// whole reason to publish the first. Saying so here costs a sentence and stops the table being
    /// read as a map of the world's conflicts rather than as a map of this system's reach.
    /// </para>
    /// </summary>
    private const string BreadthNote =
        "These count what reached this system, not what happened. A region low in this table is a "
        + "region this system reads little about; whether that is because little was reported or "
        + "because nothing here was looking is answered by the source list below, not by the "
        + "counts above.";


    /// <summary>
    /// Why the unplaced count is a single number rather than three. An observation the resolver could
    /// not place has no coordinate, so there is no honest way to attribute it to a theatre — and
    /// guessing from the text would be exactly the inference this system refuses everywhere else.
    /// </summary>
    private const string UnplacedNote =
        "These named a place the gazetteer does not hold, so they were kept but not drawn. They are "
        + "not split by theatre because an observation without a coordinate cannot be attributed to "
        + "one; some of them belong to the three above.";

    public async Task<CoverageReport> BuildAsync(CancellationToken cancellationToken)
    {
        var theatres = new List<TheatreCoverage>(Theatres.All.Count);

        foreach (var theatre in Theatres.All)
        {
            var totals = await repository.CountPlacedAsync(theatre, cancellationToken);

            theatres.Add(new TheatreCoverage(
                theatre.Name,
                totals.Placed,
                totals.ByPrecision,
                totals.BySource,
                lexicon.PlacesByTheatre.TryGetValue(theatre.Name, out var places) ? places : 0,
                theatre.Caveat));
        }

        var breadth = await repository.CountBreadthAsync(cancellationToken);

        return new CoverageReport(
            theatres,
            await repository.CountUnplacedAsync(cancellationToken),
            UnplacedNote,
            lexicon.AmbiguousNameCount,
            breadth.ByRegion,
            breadth.ByLanguage,
            breadth.ByTier,
            breadth.ByPlatform,
            collection.ReadOutcomes(),
            BreadthNote);
    }
}
