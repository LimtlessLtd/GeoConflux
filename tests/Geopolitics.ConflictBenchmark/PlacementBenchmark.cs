using System.Globalization;
using System.Text;
using Geopolitics.Infrastructure.Location;
using Xunit.Abstractions;

namespace Geopolitics.ConflictBenchmark;

/// <summary>
/// How much of the world's organised violence this system could place, measured against a register
/// it did not write.
/// <para>
/// The question is deliberately not "how well does it do on Ukraine, Yemen and Tigray". Those three
/// are in this repository because somebody typed them into <c>Theatres.cs</c>, and a benchmark scored
/// against the list its own author chose measures the author. UCDP recorded events for
/// <b>319 distinct conflicts</b> in 2024. This asks how many of them this system could draw at all.
/// </para>
/// <para>
/// What it measures is the <em>text</em> path, and that is the path that matters for the conflicts
/// nobody is watching. A coded dataset supplies its own coordinates and needs no gazetteer — but
/// ACLED and UCDP both require credentials, and both lag by months to years. An obscure war reaches
/// this system, if it reaches it at all, as a sentence in someone's reporting naming a place. Whether
/// that sentence can be placed is what this measures.
/// </para>
/// </summary>
public sealed class PlacementBenchmark(ITestOutputHelper output)
{
    /// <summary>
    /// Words UCDP appends to say what kind of unit a place is. They are part of the coding rather
    /// than part of the name — "Kabul city" is the city of Kabul — and a gazetteer holds the name.
    /// Stripping them is what any real adapter would do, so the benchmark does it too and reports
    /// what it was worth.
    /// </summary>
    private static readonly string[] UnitWords =
    [
        "city", "town", "village", "district", "subdistrict", "province", "oblast", "raion",
        "region", "state", "governorate", "county", "department", "municipality", "woreda",
        "zone", "prefecture", "commune", "camp", "refugee", "parish", "area", "territory",
        "division", "district'", "sub-district", "township", "borough", "canton", "circle",
    ];

    /// <summary>
    /// The floor this benchmark defends, and it is worth being clear about what that is not.
    /// <para>
    /// It is not a standard this system meets. It is set a few points below what was measured when
    /// the benchmark was written, so that a collapse — a lexicon that stops loading, a merge that
    /// starts discarding names, an extract regenerated wrongly — fails the build, while an ordinary
    /// percentage point of drift does not. Passing this test means nothing broke. It does not mean
    /// the coverage is good, and the numbers in RESULTS.md are there so that nobody can read it that
    /// way.
    /// </para>
    /// </summary>
    private const double MinimumConflictsReachable = 0.72;

    private const double MinimumTailReachable = 0.62;

    [Fact]
    public void MeasureWhatFractionOfTheWorldsConflictsThisSystemCouldPlace()
    {
        var results = ConflictRegister.All.Select(Measure).ToArray();
        var report = new StringBuilder();

        Summarise(report, results);
        WriteResults(report.ToString());

        var reachable = results.Count(r => r.PlacedEvents > 0) / (double)results.Length;
        var tail = results.Where(r => r.Conflict.Events < ConflictRegister.TailThreshold).ToArray();
        var tailReachable = tail.Count(r => r.PlacedEvents > 0) / (double)tail.Length;

        output.WriteLine(report.ToString());

        Assert.True(
            reachable >= MinimumConflictsReachable,
            $"Only {reachable:P0} of {results.Length} recorded conflicts have any place this system "
            + $"can resolve, below the {MinimumConflictsReachable:P0} floor.");

        Assert.True(
            tailReachable >= MinimumTailReachable,
            $"Only {tailReachable:P0} of the {tail.Length} least-reported conflicts have any place "
            + $"this system can resolve, below the {MinimumTailReachable:P0} floor.");
    }

    /// <summary>
    /// Resolves every place a conflict happened at, three ways, so the report can say which of them
    /// is doing the work rather than only that the total is what it is.
    /// </summary>
    private static Measurement Measure(Conflict conflict)
    {
        var country = CountryCodeFor(conflict.Country);
        var verbatim = 0;
        var stripped = 0;
        var contextual = 0;
        var placed = 0;
        var unresolved = new List<ConflictPlace>();

        foreach (var place in conflict.Places)
        {
            var bare = WithoutUnitWord(place.Name);

            if (Gazetteer.TryResolve(place.Name, out _))
            {
                verbatim += place.Events;
                placed += place.Events;
                continue;
            }

            if (Gazetteer.TryResolve(bare, out _))
            {
                stripped += place.Events;
                placed += place.Events;
                continue;
            }

            // Context last, because it is the weakest claim of the three: it settles a name the
            // lexicon holds several times over, using the country the record states.
            if (country is not null
                && (Gazetteer.TryResolve(place.Name, new PlaceContext(country), out _)
                    || Gazetteer.TryResolve(bare, new PlaceContext(country), out _)))
            {
                contextual += place.Events;
                placed += place.Events;
                continue;
            }

            unresolved.Add(place);
        }

        return new Measurement(conflict, placed, verbatim, stripped, contextual, unresolved);
    }

    /// <summary>
    /// UCDP names countries in its own historical register — "Myanmar (Burma)", "Russia (Soviet
    /// Union)". The parenthetical is the older name, and the current one is what a gazetteer holds.
    /// The ISO code is looked up rather than tabulated here, so this file states no geography of its
    /// own.
    /// </summary>
    private static string? CountryCodeFor(string name)
    {
        var open = name.IndexOf('(', StringComparison.Ordinal);
        var current = (open > 0 ? name[..open] : name).Trim();

        return Gazetteer.TryResolve(current, out var entry) ? entry.CountryCode : null;
    }

    private static string WithoutUnitWord(string name)
    {
        var trimmed = name.Trim();

        // Twice, because "Nuseirat refugee camp" carries two of them.
        for (var pass = 0; pass < 2; pass++)
        {
            var space = trimmed.LastIndexOf(' ');

            if (space <= 0)
            {
                break;
            }

            var last = trimmed[(space + 1)..].Trim('\'', '.', ',');

            if (!UnitWords.Contains(last, StringComparer.OrdinalIgnoreCase))
            {
                break;
            }

            trimmed = trimmed[..space].Trim();
        }

        return trimmed;
    }

    private sealed record Measurement(
        Conflict Conflict,
        int PlacedEvents,
        int Verbatim,
        int Stripped,
        int Contextual,
        IReadOnlyList<ConflictPlace> Unresolved)
    {
        public double Coverage => Conflict.Events == 0 ? 0 : PlacedEvents / (double)Conflict.Events;
    }

    private static void Summarise(StringBuilder report, Measurement[] results)
    {
        var events = results.Sum(r => r.Conflict.Events);
        var placed = results.Sum(r => r.PlacedEvents);
        var reachable = results.Count(r => r.PlacedEvents > 0);
        var tail = results.Where(r => r.Conflict.Events < ConflictRegister.TailThreshold).ToArray();
        var tailReachable = tail.Count(r => r.PlacedEvents > 0);

        report.AppendLine("# Conflict coverage benchmark");
        report.AppendLine();
        report.AppendLine("<!-- Generated by Geopolitics.ConflictBenchmark. Do not edit by hand. -->");
        report.AppendLine();
        report.AppendLine(
            "Ground truth is the UCDP Georeferenced Event Dataset v25.1 for 2024 — every conflict it");
        report.AppendLine(
            "recorded an event for, with the places those events happened at. It is not a list this");
        report.AppendLine(
            "repository wrote, which is the only reason scoring against it means anything.");
        report.AppendLine();
        report.AppendLine("## The headline");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Conflicts UCDP recorded in 2024 | {results.Length} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Theatres this system is configured to watch | 3 |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Conflicts with at least one place it can resolve | {reachable} ({reachable / (double)results.Length:P0}) |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Recorded events at a place it can resolve | {placed:N0} of {events:N0} ({placed / (double)events:P0}) |");
        report.AppendLine();
        report.AppendLine(
            "The first two rows are the finding. Placement is no longer the binding constraint — the");
        report.AppendLine(
            "lexicon reaches nearly all of these conflicts — but the register of what to *watch* is");
        report.AppendLine(
            "still three entries long and hand-written, so the map's emptiness elsewhere is a");
        report.AppendLine("statement about a source list, not about the world.");
        report.AppendLine();

        report.AppendLine("## The conflicts nobody is following");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"UCDP could source fewer than {ConflictRegister.TailThreshold} events for {tail.Length} of the {results.Length} conflicts. These are the ones");
        report.AppendLine(
            "a system is actually tested by: anything can cover Ukraine, and covering Ukraine proves");
        report.AppendLine("nothing about whether the Murle–Nuer fighting in Jonglei is visible.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Conflicts under {ConflictRegister.TailThreshold} recorded events | {tail.Length} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| …with at least one resolvable place | {tailReachable} ({tailReachable / (double)tail.Length:P0}) |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| …with none at all | {tail.Length - tailReachable} |");
        report.AppendLine();

        report.AppendLine("## What resolves them");
        report.AppendLine();
        report.AppendLine("| Path | Events placed |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| The name as UCDP writes it | {results.Sum(r => r.Verbatim):N0} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| After dropping the unit word (\"Kabul city\" → \"Kabul\") | {results.Sum(r => r.Stripped):N0} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Only once the record's country narrows it | {results.Sum(r => r.Contextual):N0} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| Not at all | {events - placed:N0} |");
        report.AppendLine();

        report.AppendLine("## By region");
        report.AppendLine();
        report.AppendLine("| Region | Conflicts | Reachable | Events placed |");
        report.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var group in results.GroupBy(r => r.Conflict.Region).OrderByDescending(g => g.Count()))
        {
            var groupEvents = group.Sum(r => r.Conflict.Events);
            var groupPlaced = group.Sum(r => r.PlacedEvents);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {group.Count()} | {group.Count(r => r.PlacedEvents > 0)} | {groupPlaced / (double)groupEvents:P0} |");
        }

        report.AppendLine();
        report.AppendLine("## The worst-covered conflicts with real casualties");
        report.AppendLine();
        report.AppendLine(
            "Ordered by deaths, so this is not a list of trivia. A conflict here killed people and");
        report.AppendLine("this system could place little or none of where it happened.");
        report.AppendLine();
        report.AppendLine("| Conflict | Country | Events | Deaths | Placed |");
        report.AppendLine("| --- | --- | ---: | ---: | ---: |");

        foreach (var result in results
            .Where(r => r.Coverage < 0.5)
            .OrderByDescending(r => r.Conflict.Deaths)
            .Take(20))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {result.Conflict.Name} | {result.Conflict.Country} | {result.Conflict.Events} | {result.Conflict.Deaths:N0} | {result.Coverage:P0} |");
        }

        report.AppendLine();
        report.AppendLine("## Reading these numbers");
        report.AppendLine();
        report.AppendLine(
            "**This measures reach, not knowledge.** A resolvable place name means a report naming it");
        report.AppendLine(
            "could be drawn in roughly the right spot. It does not mean anything was reported, that");
        report.AppendLine(
            "this deployment reads a source covering it, or that the placement is precise — most of");
        report.AppendLine(
            "the world outside the tasked theatres resolves to a district or province centroid.");
        report.AppendLine();
        report.AppendLine(
            "**The dataset lags.** UCDP v25.1 ends at 2024. A conflict that began or resumed since");
        report.AppendLine(
            "then is absent from this register entirely, which is the strongest argument for the text");
        report.AppendLine("path this benchmark measures: the datasets find out late.");
        report.AppendLine();
        report.AppendLine(
            "**Coverage of a conflict is not coverage of its victims.** Events are weighted equally");
        report.AppendLine(
            "here regardless of how many died, because the question is whether the system can see the");
        report.AppendLine("fighting, not how bad it was.");
    }

    private static void WriteResults(string content)
    {
        // Alongside the register rather than in the build output, so the numbers land in a diff and a
        // change in coverage is something a reviewer sees rather than something they have to re-run.
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "data", "conflict-benchmark", "RESULTS.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
