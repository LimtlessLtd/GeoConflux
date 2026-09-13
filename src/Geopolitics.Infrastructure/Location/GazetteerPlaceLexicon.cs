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

    /// <summary>
    /// Both sourced layers added together, because the ceiling for a country is everything the
    /// lexicon holds there and a reader has no reason to care which extract supplied it.
    /// </summary>
    public IReadOnlyDictionary<string, int> PlacesByCountry { get; } = Combine();

    public int AmbiguousNameCount => Gazetteer.AmbiguousSourcedNames;

    private static Dictionary<string, int> Combine()
    {
        var counts = new Dictionary<string, int>(GlobalPlaces.CountsByCountry, StringComparer.Ordinal);

        foreach (var (country, places) in TheatrePlaces.CountsByCountry)
        {
            counts[country] = counts.GetValueOrDefault(country) + places;
        }

        return counts;
    }
}
