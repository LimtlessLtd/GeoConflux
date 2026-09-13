using Geopolitics.Application.Abstractions;

namespace Geopolitics.Application.Coverage;

/// <param name="Theatre">The theatre's short name, such as <c>Tigray</c>.</param>
/// <param name="PlacedCount">Observations drawn on the map inside this theatre.</param>
/// <param name="ByPrecision">How precisely each was placed, coarsest information last.</param>
/// <param name="BySource">Which sources contributed, busiest first.</param>
/// <param name="GazetteerPlaces">
/// How many place names the lexicon holds for this theatre. The ceiling on what any text source can
/// ever place here, and the figure that explains why one theatre looks emptier than another.
/// </param>
/// <param name="Caveat">What this theatre's numbers cannot tell the reader.</param>
public sealed record TheatreCoverage(
    string Theatre,
    int PlacedCount,
    IReadOnlyList<CategoryCount> ByPrecision,
    IReadOnlyList<CategoryCount> BySource,
    int GazetteerPlaces,
    string Caveat);

/// <param name="Theatres">One entry per theatre, in the order they are defined.</param>
/// <param name="UnplacedCount">Observations that named a place the gazetteer could not resolve.</param>
/// <param name="UnplacedNote">Why that number is not broken down by theatre.</param>
/// <param name="AmbiguousNameCount">
/// Place names dropped from the sourced lexicon because they denote more than one place. A direct,
/// countable limit on coverage rather than an abstract one.
/// </param>
/// <param name="ByRegion">Placed observations per country code, busiest first.</param>
/// <param name="ByLanguage">Observations per language tag, including an explicit unknown.</param>
/// <param name="ByTier">Published reporting against user-generated claims.</param>
/// <param name="ByPlatform">Claims per open platform.</param>
/// <param name="Sources">
/// What each source the last collection runs asked actually gave. The entries that gave nothing are
/// the point: a source that refused, a source that publishes nothing, and a source read that had
/// nothing relevant to say are three different statements, and an empty space on a map is none of
/// them.
/// </param>
/// <param name="BreadthNote">What these counts do and do not establish.</param>
public sealed record CoverageReport(
    IReadOnlyList<TheatreCoverage> Theatres,
    int UnplacedCount,
    string UnplacedNote,
    int AmbiguousNameCount,
    IReadOnlyList<CategoryCount> ByRegion,
    IReadOnlyList<CategoryCount> ByLanguage,
    IReadOnlyList<CategoryCount> ByTier,
    IReadOnlyList<CategoryCount> ByPlatform,
    IReadOnlyList<SourceOutcome> Sources,
    string BreadthNote);
