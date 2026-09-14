using System.Globalization;

namespace Geopolitics.ConflictBenchmark;

/// <param name="Name">The place as UCDP names it, which is not necessarily as a gazetteer names it.</param>
/// <param name="Events">How many of the conflict's events happened there.</param>
public sealed record ConflictPlace(string Name, int Events);

/// <param name="Id">UCDP's conflict identifier, stable across dataset versions.</param>
/// <param name="Name">UCDP's name for the conflict, such as <c>Ethiopia: Tigray</c>.</param>
/// <param name="Violence">state-based, non-state, or one-sided, as UCDP codes it.</param>
/// <param name="Country">The country most of its events were recorded in.</param>
/// <param name="Places">Where its events happened, busiest first.</param>
public sealed record Conflict(
    string Id,
    string Name,
    string SideA,
    string SideB,
    string Region,
    string Violence,
    string Country,
    int Events,
    int Deaths,
    IReadOnlyList<ConflictPlace> Places);

/// <summary>
/// The benchmark's ground truth: every conflict UCDP recorded an event for in one year.
/// <para>
/// It is loaded rather than written here, and that is the point of it. A list of conflicts this
/// repository chose would measure whether this repository can find the conflicts it chose. UCDP's
/// register was compiled by people with no interest in how this system scores against it.
/// </para>
/// </summary>
public static class ConflictRegister
{
    public static IReadOnlyList<Conflict> All { get; } = Load();

    /// <summary>
    /// Below this many recorded events in a year, a conflict is one that even the standard
    /// event-coding project could only source a handful of incidents for. These are the ones worth
    /// reporting separately: any system will cover Ukraine, and covering Ukraine proves very little.
    /// </summary>
    public const int TailThreshold = 25;

    public static IReadOnlyList<Conflict> Tail => [.. All.Where(c => c.Events < TailThreshold)];

    private static Conflict[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "conflict-benchmark", "conflicts.tsv");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The conflict register is missing from {path}. It is produced by "
                + "tools/conflicts/extract.py and committed; a benchmark that ran over zero conflicts "
                + "would report that this system covers all of them.");
        }

        var conflicts = new List<Conflict>();

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');

            if (fields.Length != 10)
            {
                throw new InvalidOperationException(
                    $"A row of the conflict register has {fields.Length} fields rather than 10. The "
                    + "register and this reader disagree about the format, and a benchmark run "
                    + "against a misread register is worse than no benchmark.");
            }

            conflicts.Add(new Conflict(
                fields[0],
                fields[1],
                fields[2],
                fields[3],
                fields[4],
                fields[5],
                fields[6],
                int.Parse(fields[7], CultureInfo.InvariantCulture),
                int.Parse(fields[8], CultureInfo.InvariantCulture),
                ParsePlaces(fields[9])));
        }

        if (conflicts.Count == 0)
        {
            throw new InvalidOperationException("The conflict register holds no conflicts.");
        }

        return [.. conflicts];
    }

    private static ConflictPlace[] ParsePlaces(string field)
    {
        if (field.Length == 0)
        {
            return [];
        }

        var places = new List<ConflictPlace>();

        foreach (var entry in field.Split('|'))
        {
            var split = entry.LastIndexOf('*');

            if (split > 0 && int.TryParse(entry[(split + 1)..], CultureInfo.InvariantCulture, out var events))
            {
                places.Add(new ConflictPlace(entry[..split], events));
            }
        }

        return [.. places];
    }
}
