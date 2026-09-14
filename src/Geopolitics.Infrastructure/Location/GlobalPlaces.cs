using System.Globalization;
using System.Reflection;

namespace Geopolitics.Infrastructure.Location;

/// <param name="Name">Preferred name, English where the source had one.</param>
/// <param name="Aliases">The same place as written in the languages that report it.</param>
/// <param name="Rank">
/// How large an administrative unit this is, smaller meaning larger: 1 a first-order division,
/// 2 a second-order division, 3 the settlement that is the seat of one. Carried for the same reason
/// the theatre extract carries it — nested units share a name constantly, and a merge that read
/// those as "this name means two different places" would drop the most-reported place in a country.
/// </param>
/// <param name="Population">Inhabitants where the source records a figure, used only to break ties.</param>
/// <param name="Admin1">
/// The first-order administrative unit this place is in, in the source's own coding. An ADM1 row
/// carries its own code here, so a unit contains itself.
/// </param>
/// <param name="Admin2">
/// The second-order unit, where the source records one. Frequently blank even for places that plainly
/// sit inside one, which is why containment falls back on distance rather than on this alone.
/// </param>
public sealed record GlobalPlace(
    string Name,
    double Latitude,
    double Longitude,
    string CountryCode,
    PlacePrecision Precision,
    int Rank,
    int? Population,
    string Admin1,
    string Admin2,
    IReadOnlyList<string> Aliases);

/// <summary>
/// The global coarse place layer: every first- and second-order administrative unit on earth and the
/// settlement that is the seat of each, loaded from the committed GeoNames extract.
/// <para>
/// This is the tier that makes a report from a country nobody has tasked drawable at all. It is
/// deliberately coarse — it holds districts and district towns, not villages — and the depth below it
/// is bought a theatre at a time. See [ADR 033](../../../docs/adr/033-tiered-gazetteer-artefact.md).
/// </para>
/// <para>
/// One line per place rather than JSON, because at 78,547 places the encoding is most of the file:
/// the same data is 6.1 MB this way and 19.2 MB as indented JSON, and a changed place is one changed
/// line in a diff rather than twelve. The cost of that choice is that the parse has to be defended
/// rather than handed to a deserialiser, which is what most of this file is.
/// </para>
/// </summary>
public static class GlobalPlaces
{
    private const string ResourceName = "Geopolitics.Infrastructure.Location.Data.global-places.tsv";

    /// <summary>
    /// How malformed the file may be before it is treated as the wrong file rather than as a file
    /// with a bad line in it.
    /// <para>
    /// A single unreadable row is a defect worth counting and surviving: losing one district is not
    /// worth refusing to start. A file where a tenth of the rows do not parse is not this format, and
    /// carrying on would mean running with a lexicon that is mostly missing while everything still
    /// builds — which is the failure that looks like working software.
    /// </para>
    /// </summary>
    private const double UnreadableRowLimit = 0.1;

    private static readonly GlobalPlace[] Entries = Load();

    /// <summary>Every extracted place, in the order the extract records them.</summary>
    public static IReadOnlyList<GlobalPlace> All => Entries;

    /// <summary>
    /// Rows the parse could not read. Zero for a committed extract, and reported rather than hidden
    /// because a number that silently stops being zero is how a format drift goes unnoticed.
    /// </summary>
    public static int UnreadableRows { get; private set; }

    /// <summary>
    /// How many places the extract holds per ISO country code.
    /// <para>
    /// This is the ceiling on what any text report from that country can ever place, and it is wildly
    /// uneven — Colombia has 2,200 and South Sudan has 48. Reported per country rather than per
    /// theatre because at global scale the theatre is no longer the unit a reader cares about.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountsByCountry { get; } =
        Entries.GroupBy(entry => entry.CountryCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static GlobalPlace[] Load()
    {
        using var stream = typeof(GlobalPlaces).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded gazetteer data '{ResourceName}' is missing from "
                + $"{Assembly.GetExecutingAssembly().GetName().Name}. It is produced by "
                + "tools/gazetteer/global.py and committed; the build does not fetch it.");

        using var reader = new StreamReader(stream);
        var (places, unreadable) = Parse(reader);

        if (places.Length == 0)
        {
            // An empty extract would leave every report outside the three deep theatres unplaceable
            // while everything still built and ran.
            throw new InvalidOperationException("The embedded global gazetteer data contains no places.");
        }

        if (unreadable > (places.Length + unreadable) * UnreadableRowLimit)
        {
            throw new InvalidOperationException(
                $"{unreadable} of {places.Length + unreadable} rows in the global gazetteer extract could "
                + "not be read, which means this is not the format the loader expects rather than a file "
                + "with a bad line in it.");
        }

        UnreadableRows = unreadable;
        return places;
    }

    /// <summary>
    /// Reads the extract, one place per line.
    /// <para>
    /// Separate from <see cref="Load"/> and taking a reader rather than reaching for the embedded
    /// resource, so the shapes this has to survive — a short row, a number that is not one, a
    /// precision letter nobody wrote — can be asserted directly instead of only through a file that
    /// by construction has none of them.
    /// </para>
    /// </summary>
    internal static (GlobalPlace[] Places, int Unreadable) Parse(TextReader reader)
    {
        var places = new List<GlobalPlace>();
        var unreadable = 0;

        while (reader.ReadLine() is { } line)
        {
            // The header carries provenance and the column order, so the file says what it is without
            // a reader having to find the script that wrote it.
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (TryParse(line, out var place))
            {
                places.Add(place);
            }
            else
            {
                unreadable++;
            }
        }

        return ([.. places], unreadable);
    }

    private static bool TryParse(string line, out GlobalPlace place)
    {
        place = null!;

        var fields = line.Split('\t');

        // Ten fields exactly. Fewer means a truncated row; more means a tab reached a name, which
        // the extractor refuses to emit and which would otherwise shift every column after it.
        if (fields.Length != 10)
        {
            return false;
        }

        var name = fields[0];

        if (name.Length == 0 || fields[3].Length == 0)
        {
            return false;
        }

        if (!double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            || !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
        {
            return false;
        }

        // A coordinate outside the possible range is not a place that is slightly wrong, it is a
        // column that has shifted. Drawing it would put a marker somewhere no marker can go.
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return false;
        }

        if (ParsePrecision(fields[4]) is not { } precision)
        {
            return false;
        }

        int? population = int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        place = new GlobalPlace(
            name,
            latitude,
            longitude,
            fields[3],
            precision,
            rank,
            population,
            fields[7],
            fields[8],
            fields[9].Length == 0 ? [] : fields[9].Split('|'));

        return true;
    }

    /// <summary>
    /// The precision letter the extract writes.
    /// <para>
    /// An unrecognised letter is a refusal rather than a default, unlike the theatre extract's reader
    /// which falls back to <see cref="PlacePrecision.Region"/>. The difference is what the field is:
    /// there it is a word that could be spelled unexpectedly, here it is one of three letters, and a
    /// fourth means the row is not the row it appears to be.
    /// </para>
    /// </summary>
    private static PlacePrecision? ParsePrecision(string precision) => precision switch
    {
        "S" => PlacePrecision.Settlement,
        "R" => PlacePrecision.Region,
        "C" => PlacePrecision.Country,
        _ => null,
    };
}
