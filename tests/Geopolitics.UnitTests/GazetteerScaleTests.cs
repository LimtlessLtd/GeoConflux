using System.Diagnostics;
using Geopolitics.Infrastructure.Location;
using Xunit.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// Keeps the lexicon's cost visible as it grows.
/// <para>
/// The two entry points have very different shapes, and the difference decides how large this table
/// can safely get. <see cref="Gazetteer.TryResolve"/> is a frozen-dictionary lookup and does not care
/// how many entries there are. <see cref="Gazetteer.FindFirstMention"/> scans every searchable
/// spelling against the text, so its cost is linear in the size of the lexicon — and it runs on every
/// observation the offline enrichment provider sees, which under the default configuration is every
/// observation the published pipeline processes.
/// </para>
/// <para>
/// The bound below is deliberately loose, in the tradition of the pipeline throughput test: it exists
/// to catch the day someone adds a zero to the gazetteer and turns a millisecond into a second, not
/// to police microseconds on a noisy CI runner. The measured figure is written to the test output so
/// the actual number is visible in the log even while the assertion stays generous.
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
        output.WriteLine($"FindFirstMention: {perScan:F3} ms per scan over {Iterations} iterations.");

        Assert.Equal("Odesa", Gazetteer.FindFirstMention(Report));
        Assert.True(
            perScan < 5.0,
            $"A single scan took {perScan:F3} ms. At that cost the offline enrichment provider becomes "
            + "the slowest part of the pipeline, and the lexicon needs an index rather than a linear scan.");
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
}
