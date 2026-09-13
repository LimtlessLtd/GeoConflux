using Geopolitics.Application.Abstractions;

namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// Reports what the local lexicon holds, so the coverage statement can state a ceiling rather than
/// leave a reader to infer one from an empty patch of map.
/// <para>
/// A thin adapter on purpose. The figures are static properties of tables built once at startup, and
/// the only reason this type exists at all is that the lexicon lives in infrastructure while the
/// report that uses it is application logic.
/// </para>
/// </summary>
public sealed class GazetteerPlaceLexicon : IPlaceLexicon
{
    public IReadOnlyDictionary<string, int> PlacesByTheatre => TheatrePlaces.CountsByTheatre;

    public int AmbiguousNameCount => Gazetteer.AmbiguousSourcedNames;
}
