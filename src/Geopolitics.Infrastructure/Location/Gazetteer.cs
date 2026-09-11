using System.Collections.Frozen;

namespace Geopolitics.Infrastructure.Location;

/// <param name="CanonicalName">Preferred display name for the place.</param>
/// <param name="Latitude">Representative latitude.</param>
/// <param name="Longitude">Representative longitude.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 code, where the place sits in one country.</param>
public sealed record GazetteerEntry(string CanonicalName, double Latitude, double Longitude, string? CountryCode);

/// <summary>
/// The local place lexicon: chokepoints, seas, and cities that recur in geopolitical reporting.
/// Coordinates are representative centroids, adequate for a globe view at these zoom levels.
/// <para>
/// It is a single shared table because two components need the same names for different reasons.
/// The resolver turns a name into a position, which per ADR 005 only it may do. The mock enrichment
/// provider needs to <em>recognise</em> a name in text so the offline demo exercises the real
/// name-then-resolve path rather than a shortcut. Two copies of this list would drift, and the demo
/// would start proving something the production path does not do.
/// </para>
/// </summary>
public static class Gazetteer
{
    private static readonly GazetteerEntry[] Entries =
    [
        new("Bab-el-Mandeb", 12.585, 43.334, "DJ"),
        new("Strait of Hormuz", 26.567, 56.250, "OM"),
        new("Suez Canal", 30.500, 32.350, "EG"),
        new("Red Sea", 20.000, 38.000, null),
        new("Gulf of Aden", 12.500, 47.500, null),
        new("Black Sea", 43.000, 34.000, null),
        new("Eastern Mediterranean", 34.700, 33.900, null),
        new("South China Sea", 13.000, 114.000, null),
        new("Taiwan Strait", 24.500, 119.500, null),
        new("Strait of Malacca", 3.000, 100.500, "MY"),
        new("Gulf of Guinea", 3.000, 3.000, null),
        new("Persian Gulf", 26.500, 51.500, null),
        new("Baltic Sea", 57.500, 19.500, null),
        new("Kerch Strait", 45.300, 36.500, null),
        new("Panama Canal", 9.080, -79.680, "PA"),
        new("Sea of Japan", 40.000, 135.000, null),
        new("Kyiv", 50.450, 30.523, "UA"),
        new("Odesa", 46.482, 30.723, "UA"),
        new("Moscow", 55.756, 37.617, "RU"),
        new("Beijing", 39.904, 116.407, "CN"),
        new("Taipei", 25.033, 121.565, "TW"),
        new("Tehran", 35.689, 51.389, "IR"),
        new("Sanaa", 15.369, 44.191, "YE"),
        new("Aden", 12.785, 45.019, "YE"),
        new("Djibouti", 11.572, 43.145, "DJ"),
        new("Cairo", 30.044, 31.236, "EG"),
        new("Beirut", 33.888, 35.495, "LB"),
        new("Damascus", 33.513, 36.292, "SY"),
        new("Jerusalem", 31.769, 35.217, "IL"),
        new("Gaza", 31.504, 34.466, "PS"),
        new("Baghdad", 33.315, 44.366, "IQ"),
        new("Riyadh", 24.713, 46.675, "SA"),
        new("Ankara", 39.933, 32.859, "TR"),
        new("Istanbul", 41.008, 28.978, "TR"),
        new("Khartoum", 15.501, 32.559, "SD"),
        new("Mogadishu", 2.047, 45.318, "SO"),
        new("Bamako", 12.639, -8.003, "ML"),
        new("Lagos", 6.524, 3.379, "NG"),
        new("Manila", 14.600, 120.984, "PH"),
        new("Seoul", 37.567, 126.978, "KR"),
        new("Pyongyang", 39.039, 125.762, "KP"),
        new("Tokyo", 35.690, 139.692, "JP"),
        new("New Delhi", 28.614, 77.209, "IN"),
        new("Islamabad", 33.684, 73.048, "PK"),
        new("Kabul", 34.556, 69.208, "AF"),
        new("Caracas", 10.481, -66.904, "VE"),
        new("Port-au-Prince", 18.594, -72.307, "HT"),
        new("Brussels", 50.851, 4.352, "BE"),
        new("Warsaw", 52.230, 21.011, "PL"),
        new("Vilnius", 54.687, 25.280, "LT"),
        new("Helsinki", 60.170, 24.938, "FI"),
        new("Washington", 38.895, -77.037, "US"),
        new("London", 51.507, -0.128, "GB"),
        new("Paris", 48.857, 2.352, "FR"),
        new("Berlin", 52.520, 13.405, "DE"),
    ];

    /// <summary>Common alternates, so ordinary reporting language resolves without an exact match.</summary>
    private static readonly (string Alias, string Canonical)[] Aliases =
    [
        ("Bab al-Mandab", "Bab-el-Mandeb"),
        ("Bab el Mandeb", "Bab-el-Mandeb"),
        ("Hormuz", "Strait of Hormuz"),
        ("Malacca Strait", "Strait of Malacca"),
        ("Kiev", "Kyiv"),
        ("Odessa", "Odesa"),
        ("Sana'a", "Sanaa"),
        ("Gaza Strip", "Gaza"),
        ("Washington DC", "Washington"),
        ("Mediterranean", "Eastern Mediterranean"),
        ("Levant", "Eastern Mediterranean"),
    ];

    private static readonly FrozenDictionary<string, GazetteerEntry> Lookup = BuildLookup();

    /// <summary>
    /// Every searchable spelling with the entry it denotes, longest first so that "Strait of Hormuz"
    /// is preferred over the bare "Hormuz" it contains.
    /// </summary>
    private static readonly (string Term, GazetteerEntry Entry)[] SearchTerms = BuildSearchTerms();

    public static bool TryResolve(string? name, out GazetteerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            entry = null!;
            return false;
        }

        return Lookup.TryGetValue(NormaliseKey(name), out entry!);
    }

    /// <summary>
    /// Finds the first place this text names, or <see langword="null"/> when it names none.
    /// <para>
    /// Substring matching, not tokenisation: the lexicon holds multi-word names and the input is
    /// prose. The earliest mention wins because reporting states where something happened before it
    /// lists which other places reacted to it.
    /// </para>
    /// </summary>
    public static string? FindFirstMention(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var bestIndex = int.MaxValue;
        string? bestName = null;

        foreach (var (term, entry) in SearchTerms)
        {
            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (index >= 0 && index < bestIndex)
            {
                bestIndex = index;
                bestName = entry.CanonicalName;
            }
        }

        return bestName;
    }

    /// <summary>Case- and punctuation-insensitive key, so "Bab el Mandeb" matches "Bab-el-Mandeb".</summary>
    public static string NormaliseKey(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }

    private static FrozenDictionary<string, GazetteerEntry> BuildLookup()
    {
        var map = new Dictionary<string, GazetteerEntry>(StringComparer.Ordinal);

        foreach (var entry in Entries)
        {
            map[NormaliseKey(entry.CanonicalName)] = entry;
        }

        foreach (var (alias, canonical) in Aliases)
        {
            map[NormaliseKey(alias)] = map[NormaliseKey(canonical)];
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static (string Term, GazetteerEntry Entry)[] BuildSearchTerms()
    {
        var terms = new List<(string Term, GazetteerEntry Entry)>(Entries.Length + Aliases.Length);
        terms.AddRange(Entries.Select(entry => (entry.CanonicalName, entry)));

        foreach (var (alias, canonical) in Aliases)
        {
            terms.Add((alias, Entries.First(entry => entry.CanonicalName == canonical)));
        }

        return [.. terms.OrderByDescending(item => item.Term.Length)];
    }
}
