using System.Diagnostics;
using Geopolitics.Infrastructure.Location;
using Xunit.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// Keeps the lexicon's cost visible as it grows.
/// <para>
/// The two entry points have very different shapes. <see cref="Gazetteer.TryResolve"/> is a
/// frozen-dictionary lookup and does not care how many entries there are.
/// <see cref="Gazetteer.FindFirstMention"/> reads the text, and it runs on every observation the
/// offline enrichment provider sees — which under the default configuration of ADR 013 is every
/// observation the published pipeline processes.
/// </para>
/// <para>
/// That second one used to scan the text once per spelling, so its cost was linear in the size of the
/// lexicon. ADR 026 measured it at 0.525 ms against ~11,900 spellings and named the condition that
/// would end it: <em>"a lexicon ten times this size would cost five milliseconds per observation and
/// the algorithm would need replacing."</em> Sprint 10 made the lexicon global, and the scan is now
/// an Aho-Corasick automaton — so the bound below is no longer a budget being spent down, and the
/// last test here is the one that says why.
/// </para>
/// <para>
/// The bounds are deliberately loose, in the tradition of the pipeline throughput test: they exist to
/// catch the day someone turns a millisecond into a second, not to police microseconds on a noisy CI
/// runner. The measured figures are written to the test output so the actual numbers stay visible in
/// the log even while the assertions stay generous.
/// </para>
/// </summary>
public sealed class GazetteerScaleTests(ITestOutputHelper output)
{
    /// <summary>
    /// A paragraph of the kind of prose the mock provider actually reads, naming a place late so the
    /// scan cannot stop early. This is the worst realistic case rather than the average one.
    /// </summary>
    private const string Report =
        "Reporting through the day described a series of developments without naming anywhere in "
        + "particular, and much of the wire copy repeated earlier summaries at length before adding "
        + "anything new. Analysts cautioned that the picture was incomplete and that several claims "
        + "remained uncorroborated by the evening. Only in the final paragraph did the account state "
        + "that the incident had taken place in Odesa.";

    private const int Iterations = 2_000;

    [Fact]
    public void ScanningProseForAPlaceNameStaysFastEnoughToRunOnEveryObservation()
    {
        // Warm the static tables and the JIT, so the measurement is of the scan rather than of the
        // first-touch cost of building the lexicon.
        for (var index = 0; index < 50; index++)
        {
            Gazetteer.FindFirstMention(Report);
        }

        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < Iterations; index++)
        {
            Gazetteer.FindFirstMention(Report);
        }

        stopwatch.Stop();

        var perScan = stopwatch.Elapsed.TotalMilliseconds / Iterations;
        output.WriteLine(
            $"FindFirstMention: {perScan:F3} ms per scan over {Iterations} iterations, "
            + $"against {Gazetteer.SearchTermCount:N0} spellings in {Gazetteer.AutomatonStates:N0} states.");

        Assert.Equal("Odesa", Gazetteer.FindFirstMention(Report));
        Assert.True(
            perScan < 5.0,
            $"A single scan took {perScan:F3} ms. At that cost the offline enrichment provider becomes "
            + "the slowest part of the pipeline.");
    }

    [Fact]
    public void ResolvingANameByLookupDoesNotDependOnHowLargeTheLexiconIs()
    {
        for (var index = 0; index < 50; index++)
        {
            Gazetteer.TryResolve("Odesa", out _);
        }

        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < Iterations; index++)
        {
            Gazetteer.TryResolve("Odesa", out _);
        }

        stopwatch.Stop();

        var perLookup = stopwatch.Elapsed.TotalMilliseconds / Iterations;
        output.WriteLine($"TryResolve: {perLookup:F5} ms per lookup over {Iterations} iterations.");

        // This is the path every real placement takes — the resolver, and both coded-event adapters.
        // It is a hash lookup, so the assertion is really that it has not quietly become something
        // else.
        Assert.True(perLookup < 0.1, $"A single lookup took {perLookup:F5} ms, which is not a hash lookup.");
    }

    /// <summary>
    /// The property the automaton was adopted for, asserted directly rather than inferred from a
    /// wall-clock figure that a loaded CI runner can move on its own.
    /// <para>
    /// A linear scan over a hundredfold larger term set costs a hundred times as much. An automaton
    /// reads the text once whatever it is looking for, so the same text against 200 spellings and
    /// against 200,000 should cost roughly the same. The bound is a factor of eight rather than one,
    /// because there is a real effect here and it is not zero — a larger automaton touches more
    /// memory and misses cache more often — but it is a constant factor and not a multiple of the
    /// term count, and the difference between those two is the whole reason for the rewrite.
    /// </para>
    /// </summary>
    [Fact]
    public void ScanCostDoesNotGrowWithTheNumberOfSpellingsBeingLookedFor()
    {
        var small = Build(200);
        var large = Build(200_000);

        var smallCost = Cost(small);
        var largeCost = Cost(large);
        var ratio = largeCost / smallCost;

        output.WriteLine(
            $"200 spellings: {smallCost:F4} ms per scan. 200,000 spellings: {largeCost:F4} ms per scan. "
            + $"Ratio {ratio:F2}x for a 1,000x larger term set.");

        Assert.True(
            ratio < 8.0,
            $"Scanning against 1,000 times as many spellings cost {ratio:F1} times as much, which is "
            + "the shape of a linear scan rather than of an automaton.");
    }

    private static PlaceNameAutomaton Build(int terms) =>
        PlaceNameAutomaton.Build([.. Enumerable.Range(0, terms).Select(index => $"placename{index:D7}")]);

    private static double Cost(PlaceNameAutomaton automaton)
    {
        for (var index = 0; index < 50; index++)
        {
            automaton.FindFirst(Report);
        }

        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < Iterations; index++)
        {
            automaton.FindFirst(Report);
        }

        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds / Iterations;
    }
}
