using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.Infrastructure.Location;

/// <param name="Name">Preferred name, English where the source had one.</param>
/// <param name="Aliases">The same place as written in the languages that report it.</param>
/// <param name="Theatre">Which of the three theatres this place belongs to.</param>
/// <param name="Rank">
/// How large an administrative unit this is, smaller meaning larger: 1 a governorate or region,
/// 2 a district or woreda, 3 a settlement. Carried because nested units share a name constantly —
/// Marib is a governorate, a district and a city — and a merge that read those as "this name means
/// two different places" would drop the most-reported place in a theatre.
/// </param>
/// <param name="Population">Inhabitants where the source records a figure, used only to break ties.</param>
public sealed record TheatrePlace(
    string Name,
    double Latitude,
    double Longitude,
    string CountryCode,
    string Theatre,
    PlacePrecision Precision,
    int Rank,
    int? Population,
    IReadOnlyList<string> Aliases);

/// <summary>
/// The theatre place names, loaded from the committed Wikidata extract.
/// <para>
/// Separate from the curated table in <see cref="Gazetteer"/> because the two have different
/// provenance and different rules, and ADR 026 turns on keeping that distinction visible. The curated
/// entries are editorial judgements a person made and argued for in comments; these are rows a
/// recorded query produced. Nobody here wrote these coordinates, which is the entire reason they can
/// be trusted — a hand-typed latitude is indistinguishable in the file from a remembered one.
/// </para>
/// <para>
/// Embedded rather than read from disk so the lexicon travels with the assembly, exactly as the
/// replay script and the severity corpus already do, and so a published single-file build has it.
/// </para>
/// </summary>
public static class TheatrePlaces
{
    private const string ResourceName = "Geopolitics.Infrastructure.Location.Data.theatre-places.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly TheatrePlace[] Entries = Load();

    /// <summary>Every extracted place, in the order the extract records them.</summary>
    public static IReadOnlyList<TheatrePlace> All => Entries;

    /// <summary>
    /// How many places the extract holds per theatre. Reported rather than implied: a reader is
    /// entitled to know that Tigray is covered by a few dozen names and Ukraine by a few thousand,
    /// because that difference is most of why one map looks sparser than the other.
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountsByTheatre { get; } =
        Entries.GroupBy(entry => entry.Theatre, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static TheatrePlace[] Load()
    {
        using var stream = typeof(TheatrePlaces).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded gazetteer data '{ResourceName}' is missing from "
                + $"{Assembly.GetExecutingAssembly().GetName().Name}. It is produced by "
                + "tools/gazetteer/extract.py and committed; the build does not fetch it.");

        var document = JsonSerializer.Deserialize<ExtractDocument>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("The embedded gazetteer data could not be deserialised.");

        if (document.Places.Count == 0)
        {
            // An empty extract would leave every theatre report unplaceable while everything still
            // built and ran, which is the failure that looks like working software.
            throw new InvalidOperationException("The embedded gazetteer data contains no places.");
        }

        return [.. document.Places.Select(ToPlace)];
    }

    private static TheatrePlace ToPlace(ExtractedPlace place) => new(
        place.Name,
        place.Lat,
        place.Lon,
        place.Country,
        place.Theatre,
        ParsePrecision(place.Precision),
        place.Rank,
        place.Population,
        place.Aliases);

    /// <summary>
    /// The extract states a precision per selection rule: Yemen's governorates and districts are
    /// areas, so they are regions, while a town is a settlement.
    /// </summary>
    private static PlacePrecision ParsePrecision(string precision) =>
        Enum.TryParse<PlacePrecision>(precision, ignoreCase: true, out var parsed)
            ? parsed

            // Not a guess at the coarsest level for the sake of it: an unreadable precision means the
            // extract and this code disagree about the contract, and the safe reading of "something
            // is wrong" is not "this coordinate is exact".
            : PlacePrecision.Region;

    private sealed record ExtractDocument(
        [property: JsonPropertyName("places")] IReadOnlyList<ExtractedPlace> Places);

    private sealed record ExtractedPlace(
        string Name,
        double Lat,
        double Lon,
        string Country,
        string Theatre,
        string Precision,
        int Rank,
        int? Population,
        IReadOnlyList<string> Aliases);
}
