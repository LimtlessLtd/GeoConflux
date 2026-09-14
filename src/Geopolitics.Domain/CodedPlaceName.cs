namespace Geopolitics.Domain;

/// <summary>
/// Turns a place name as an event-coding project writes it into a key two records can be compared
/// on.
/// <para>
/// Coding projects append the kind of unit to the name — "Kabul city", "Pokrovsk raion", "Nuseirat
/// refugee camp" — because the coding needs to distinguish a district from the town that gives it
/// its name. The word is part of the coding, not part of the name, and a gazetteer holds the name.
/// Comparing the two without stripping it means a conflict's own coded places never match the places
/// this system resolves, which is a silent and total failure rather than a partial one.
/// </para>
/// <para>
/// This lives in the domain because both sides of that comparison do: a conflict knows the places it
/// has been fought at, and an observation knows the place it was placed at. It has no opinion about
/// where either came from.
/// </para>
/// </summary>
public static class CodedPlaceName
{
    /// <summary>
    /// The unit words, and there is nothing clever about the list: it is what these projects
    /// actually append, gathered from the data rather than from a schema. It is deliberately not
    /// extended with words that might be part of a real name — "port", "bay" and "hill" are units in
    /// one country and half the toponyms in another.
    /// </summary>
    private static readonly HashSet<string> UnitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "city", "town", "village", "district", "subdistrict", "sub-district", "province", "oblast",
        "raion", "region", "state", "governorate", "county", "department", "municipality", "woreda",
        "zone", "prefecture", "commune", "camp", "refugee", "parish", "area", "territory",
        "division", "township", "borough", "canton", "circle",
    };

    /// <summary>
    /// How many unit words may be stripped from one name. Two, because "Nuseirat refugee camp"
    /// carries two and nothing observed carries three — and an unbounded loop would eat a name like
    /// "Camp Town" down to nothing.
    /// </summary>
    private const int MaxUnitWords = 2;

    /// <summary>
    /// Drops the trailing unit words, if any. Returns the name trimmed and otherwise unchanged when
    /// there are none, and never returns empty: a name that is nothing but a unit word is left alone,
    /// because "Village" really is a place in several countries.
    /// </summary>
    public static string WithoutUnitWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();

        for (var pass = 0; pass < MaxUnitWords; pass++)
        {
            var space = trimmed.LastIndexOf(' ');

            if (space <= 0)
            {
                break;
            }

            var last = trimmed[(space + 1)..].Trim('\'', '.', ',');

            if (!UnitWords.Contains(last))
            {
                break;
            }

            trimmed = trimmed[..space].Trim();
        }

        return trimmed;
    }

    /// <summary>
    /// The comparison key: the bare name, lowercased. Case is folded because the projects and the
    /// gazetteers disagree about it constantly and none of those disagreements are about the place.
    /// </summary>
    public static string Key(string? name) => WithoutUnitWord(name).ToLowerInvariant();
}
