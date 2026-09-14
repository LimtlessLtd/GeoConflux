using System.Globalization;
using System.Reflection;
using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;

namespace Geopolitics.Infrastructure.Conflicts;

/// <summary>
/// The register of the world's armed conflicts, read from a coding project's own dataset.
/// <para>
/// Three entries used to be typed into a file in this repository. That is what a register looks like
/// when a project writes its own, and it has a defect no amount of care inside the system can fix:
/// coverage measured against it says how well this system covers the things it decided to cover, and
/// nothing at all about the rest of the world. UCDP recorded events for <b>319 distinct conflicts</b>
/// in 2024, on published criteria, with no interest in what this system can see. That is the list.
/// </para>
/// <para>
/// The geography is <em>derived</em> rather than declared, and that is the part worth understanding.
/// The extract says which places each conflict's events happened at; this resolves those names
/// through the same gazetteer the pipeline uses, and a conflict's countries are wherever its own
/// coded places turned out to be. Nobody draws a box. A conflict fought in five countries has five,
/// a conflict confined to one region of one country is held by that region's places, and both facts
/// come from the coding rather than from anybody's idea of where a war is.
/// </para>
/// </summary>
public sealed class CodedConflictRegister : IConflictRegister
{
    private const string ResourceName = "Geopolitics.Infrastructure.Conflicts.Data.ucdp-conflicts.tsv";

    /// <summary>Fields per row, as <c>tools/conflicts/extract.py</c> writes them.</summary>
    private const int Fields = 10;

    /// <summary>
    /// How malformed the extract may be before it is treated as the wrong file rather than as a file
    /// with a bad row in it. The same reasoning as the gazetteer's limit: losing one conflict is not
    /// worth refusing to start, and running with a register that is mostly missing while everything
    /// still builds is the failure that looks like working software.
    /// </summary>
    private const double UnreadableRowLimit = 0.1;

    private readonly Dictionary<string, Conflict> byKey;

    public CodedConflictRegister()
    {
        var conflicts = Load();

        AmbiguousActorWords = ConflictActorIndex.Prune(conflicts);

        // Largest first, because every consumer of this list wants it that way and because the order
        // being stable is what lets a published page be compared with the one before it.
        All = [.. conflicts.OrderByDescending(conflict => conflict.CodedEvents).ThenBy(c => c.Name, StringComparer.Ordinal)];
        byKey = All.ToDictionary(conflict => conflict.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Conflict> All { get; }

    public IReadOnlyList<string> AmbiguousActorWords { get; }

    public string Provenance =>
        $"UCDP Georeferenced Event Dataset v25.1, every conflict it recorded an event for in 2024 "
        + $"({All.Count} of them). Compiled by the Uppsala Conflict Data Program, not by this project.";

    public bool TryGet(string? key, out Conflict conflict)
    {
        conflict = null!;
        return !string.IsNullOrWhiteSpace(key) && byKey.TryGetValue(key.Trim(), out conflict!);
    }

    /// <summary>
    /// How many of the extract's place names resolved to a country. Reported rather than assumed:
    /// this is the same number the coverage benchmark measures, and a register whose geography stopped
    /// resolving would otherwise look like a world that had gone quiet.
    /// </summary>
    public int ResolvedPlaces { get; private set; }

    public int UnresolvedPlaces { get; private set; }

    private List<Conflict> Load()
    {
        using var stream = typeof(CodedConflictRegister).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The conflict register resource {ResourceName} is missing from the assembly. It is "
                + "produced by tools/conflicts/extract.py and committed; a system that started with an "
                + "empty register would report that the world holds no conflicts.");

        using var reader = new StreamReader(stream);

        var conflicts = new List<Conflict>();
        var rows = 0;
        var unreadable = 0;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            rows++;

            if (Read(line) is { } conflict)
            {
                conflicts.Add(conflict);
            }
            else
            {
                unreadable++;
            }
        }

        if (conflicts.Count == 0 || unreadable > rows * UnreadableRowLimit)
        {
            throw new InvalidOperationException(
                $"{unreadable} of {rows} rows of the conflict register could not be read, so this is "
                + "not the format this reader expects. Starting with a register that is mostly missing "
                + "would silently understate every conflict in the world.");
        }

        return conflicts;
    }

    private Conflict? Read(string line)
    {
        var fields = line.Split('\t');

        if (fields.Length != Fields || fields[0].Length == 0 || fields[1].Length == 0)
        {
            return null;
        }

        var conflict = Conflict.Coded(
            $"ucdp:{fields[0]}",
            fields[1],
            sideA: fields[2],
            sideB: fields[3],
            region: fields[4],
            violence: fields[5]);

        // The country the coding says most of this conflict's events happened in. Held as a local
        // rather than read back off the conflict, because the conflict's country set grows as the
        // places below resolve, and the context a name is settled against has to be the one fixed
        // fact about the row rather than whichever country happened to be added first.
        var stated = CountryCodeFor(fields[6]);

        // The row's totals go on once, with no place attached, so the per-place loop below can add
        // geography without also adding the events a second time.
        conflict.RecordCoded(
            placeName: null,
            countryCode: stated,
            events: Count(fields[7]),
            deaths: Count(fields[8]));

        var places = new List<(string Name, string? Country)>();
        var backing = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var place in fields[9].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = PlaceNameOf(place);

            if (name.Length == 0)
            {
                continue;
            }

            var country = CountryOf(name, stated);

            if (country is null)
            {
                UnresolvedPlaces++;
            }
            else
            {
                ResolvedPlaces++;
                backing[country] = backing.GetValueOrDefault(country) + 1;
            }

            places.Add((name, country));
        }

        foreach (var place in places)
        {
            conflict.RecordCoded(
                place.Name,
                place.Country is not null && Corroborates(backing, place.Country, stated) ? place.Country : null,
                events: 0,
                deaths: 0);
        }

        return conflict;
    }

    /// <summary>
    /// Whether a country is really part of this conflict's geography, or is one place name that
    /// happens to be spelled like a place somewhere else.
    /// <para>
    /// The distinction is not cosmetic and the data makes it plainly: of the six countries the
    /// Russia–Ukraine coding resolved into, Ukraine is backed by 378 distinct place names and Russia
    /// by 41 — both real, and the incursion into Kursk is exactly why a box drawn round Ukraine would
    /// have been wrong — while Turkey, China, Romania and the Philippines are backed by one name each.
    /// Every one of those is a collision. Left in, they would put reports from four unrelated
    /// countries into a European war on geographic grounds.
    /// </para>
    /// <para>
    /// So a country needs two of the conflict's own place names behind it. Not a share of events,
    /// which would have discarded Russia; not a hand-kept list of which countries a war "really"
    /// touches, which would be this project writing the register again through a side door. Two
    /// independent names is the point at which coincidence stops being the likelier explanation.
    /// </para>
    /// </summary>
    private static bool Corroborates(Dictionary<string, int> backing, string country, string? stated) =>
        string.Equals(country, stated, StringComparison.Ordinal)
        || backing.GetValueOrDefault(country) >= MinimumPlacesPerCountry;

    /// <summary>How many of a conflict's own coded places must resolve to a country to count it.</summary>
    private const int MinimumPlacesPerCountry = 2;

    /// <summary>
    /// Where one of a conflict's coded places is, by the same three attempts the coverage benchmark
    /// measures: the name as the coding writes it, the name without the unit word, and then the name
    /// settled by the country the conflict is mostly fought in. The third is last because it is the
    /// weakest — it resolves a name the lexicon holds several times over by leaning on context — and
    /// because a register that leaned on it first would quietly relocate places.
    /// </summary>
    private static string? CountryOf(string name, string? stated)
    {
        if (Gazetteer.TryResolve(name, out var entry))
        {
            return entry.CountryCode;
        }

        var bare = CodedPlaceName.WithoutUnitWord(name);

        if (!string.Equals(bare, name, StringComparison.Ordinal) && Gazetteer.TryResolve(bare, out entry))
        {
            return entry.CountryCode;
        }

        if (stated is null)
        {
            return null;
        }

        var scope = new PlaceContext(stated);

        return Gazetteer.TryResolve(name, scope, out entry) || Gazetteer.TryResolve(bare, scope, out entry)
            ? entry.CountryCode
            : null;
    }

    /// <summary>
    /// UCDP names countries in its own historical register — "Myanmar (Burma)", "Russia (Soviet
    /// Union)". The parenthetical is the older name and the current one is what a gazetteer holds.
    /// The code is looked up rather than tabulated, so this file states no geography of its own.
    /// </summary>
    private static string? CountryCodeFor(string name)
    {
        var open = name.IndexOf('(', StringComparison.Ordinal);
        var current = (open > 0 ? name[..open] : name).Trim();

        return Gazetteer.TryResolve(current, out var entry) ? entry.CountryCode : null;
    }

    /// <summary>Strips the <c>*count</c> the extract appends to each place name.</summary>
    private static string PlaceNameOf(string entry)
    {
        var split = entry.LastIndexOf('*');
        return (split > 0 ? entry[..split] : entry).Trim();
    }

    private static int Count(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
}
