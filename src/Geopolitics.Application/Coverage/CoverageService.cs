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
public sealed class CoverageService(ICoverageRepository repository, IPlaceLexicon lexicon) : ICoverageService
{
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

        return new CoverageReport(
            theatres,
            await repository.CountUnplacedAsync(cancellationToken),
            UnplacedNote,
            lexicon.AmbiguousNameCount);
    }
}
