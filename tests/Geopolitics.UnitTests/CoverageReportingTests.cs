using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Coverage;

namespace Geopolitics.UnitTests;

/// <summary>
/// The per-theatre coverage statement.
/// <para>
/// This exists because a map is silent about its own gaps, and silence reads as absence of events
/// rather than absence of reporting. Most of what is asserted below is that the uncomfortable parts
/// survive: that a theatre with nothing on the map still says so, that its caveat is carried whether
/// or not it has data, and that the count of things nobody could place is reported rather than
/// quietly dropped.
/// </para>
/// </summary>
public sealed class CoverageReportingTests
{
    [Fact]
    public async Task EveryTheatreIsReportedEvenWhenItHasNothingToShow()
    {
        var report = await Build(new StubCoverageRepository());

        // The empty ones are the whole point. A report that listed only theatres with data would
        // make the worst-covered theatre invisible, which is the opposite of what this is for.
        Assert.Equal(
            Theatres.All.Select(theatre => theatre.Name),
            report.Theatres.Select(coverage => coverage.Theatre));
    }

    [Fact]
    public async Task EachTheatreCarriesItsCaveatWhetherOrNotItHasData()
    {
        var report = await Build(new StubCoverageRepository
        {
            Totals = { ["Ukraine"] = new TheatreTotals(40, [new CategoryCount("Settlement", 40)], []) },
        });

        Assert.All(report.Theatres, coverage => Assert.False(string.IsNullOrWhiteSpace(coverage.Caveat)));
    }

    /// <summary>
    /// The sprint requires this statement specifically, and it is the one a reader is most likely to
    /// need: a nearly empty map of Tigray invites exactly the wrong conclusion.
    /// </summary>
    [Fact]
    public async Task TigraySaysPlainlyThatItsCoverageIsSparserThanItsConflict()
    {
        var report = await Build(new StubCoverageRepository());
        var tigray = report.Theatres.Single(coverage => coverage.Theatre == "Tigray");

        Assert.Contains("sparser than the conflict", tigray.Caveat, StringComparison.OrdinalIgnoreCase);

        // The two reasons, both checkable facts rather than hedging.
        Assert.Contains("1 July 2025", tigray.Caveat, StringComparison.Ordinal);
        Assert.Contains("blackout", tigray.Caveat, StringComparison.OrdinalIgnoreCase);

        // And the conclusion the reader should draw from an empty district.
        Assert.Contains("nobody reported", tigray.Caveat, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePlacedCountAndItsPrecisionBreakdownAgree()
    {
        var report = await Build(new StubCoverageRepository
        {
            Totals =
            {
                ["Yemen"] = new TheatreTotals(
                    12,
                    [new CategoryCount("Region", 9), new CategoryCount("Country", 3)],
                    [new CategoryCount("acled", 12)]),
            },
        });

        var yemen = report.Theatres.Single(coverage => coverage.Theatre == "Yemen");

        Assert.Equal(12, yemen.PlacedCount);
        Assert.Equal(12, yemen.ByPrecision.Sum(count => count.Count));
        Assert.Equal("acled", Assert.Single(yemen.BySource).Category);
    }

    /// <summary>
    /// The lexicon size is the ceiling on what any text source can place in a theatre, and it is
    /// most of why one theatre looks emptier than another. Reported rather than left to be inferred.
    /// </summary>
    [Fact]
    public async Task TheLexiconCeilingIsReportedPerTheatre()
    {
        var report = await Build(new StubCoverageRepository());

        Assert.Equal(2105, report.Theatres.Single(value => value.Theatre == "Ukraine").GazetteerPlaces);
        Assert.Equal(163, report.Theatres.Single(value => value.Theatre == "Tigray").GazetteerPlaces);
    }

    /// <summary>
    /// A theatre the lexicon knows nothing about reports zero rather than being omitted. Omitting it
    /// would hide the most severe coverage failure there is.
    /// </summary>
    [Fact]
    public async Task ATheatreWithNoPlaceNamesReportsZeroRatherThanBeingLeftOut()
    {
        // A new dictionary rather than `Places = { }`, which is a collection initialiser and would
        // add nothing to the defaults instead of replacing them.
        var report = await Build(
            new StubCoverageRepository(),
            new StubLexicon { Places = new Dictionary<string, int>(StringComparer.Ordinal) });

        Assert.All(report.Theatres, coverage => Assert.Equal(0, coverage.GazetteerPlaces));
        Assert.Equal(Theatres.All.Count, report.Theatres.Count);
    }

    [Fact]
    public async Task UnplacedObservationsAreReportedWithTheReasonTheyAreNotSplitByTheatre()
    {
        var report = await Build(new StubCoverageRepository { Unplaced = 7 });

        Assert.Equal(7, report.UnplacedCount);

        // Not a hedge. An observation with no coordinate genuinely cannot be attributed to a region,
        // and guessing from its text would be the inference this system refuses everywhere else.
        Assert.Contains("cannot be attributed", report.UnplacedNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NamesDroppedForAmbiguityAreReportedAsACoverageLimit()
    {
        var report = await Build(new StubCoverageRepository(), new StubLexicon { Ambiguous = 536 });

        Assert.Equal(536, report.AmbiguousNameCount);
    }

    /// <summary>
    /// Tigray is a region rather than a country, so it is the one theatre bounded inside its country.
    /// Without those limits every report from anywhere in Ethiopia would be counted as Tigray
    /// coverage, and the theatre whose thinness most needs stating would look the best covered.
    /// </summary>
    [Fact]
    public void OnlyTigrayIsBoundedInsideItsCountry()
    {
        var tigray = Theatres.All.Single(theatre => theatre.Name == "Tigray");
        var bounds = Assert.IsType<TheatreBounds>(tigray.Bounds);

        Assert.True(bounds.Contains(13.4969, 39.4769), "Mekelle should be inside the Tigray bounds.");
        Assert.False(bounds.Contains(9.145, 40.4897), "Addis Ababa should not be inside the Tigray bounds.");

        Assert.All(
            Theatres.All.Where(theatre => theatre.Name != "Tigray"),
            theatre => Assert.Null(theatre.Bounds));
    }

    private static Task<CoverageReport> Build(ICoverageRepository repository, IPlaceLexicon? lexicon = null) =>
        new CoverageService(repository, lexicon ?? new StubLexicon()).BuildAsync(CancellationToken.None);

    private sealed class StubCoverageRepository : ICoverageRepository
    {
        public Dictionary<string, TheatreTotals> Totals { get; } = new(StringComparer.Ordinal);

        public int Unplaced { get; init; }

        public Task<TheatreTotals> CountPlacedAsync(Theatre theatre, CancellationToken cancellationToken) =>
            Task.FromResult(Totals.TryGetValue(theatre.Name, out var totals)
                ? totals
                : new TheatreTotals(0, [], []));

        public Task<int> CountUnplacedAsync(CancellationToken cancellationToken) => Task.FromResult(Unplaced);
    }

    private sealed class StubLexicon : IPlaceLexicon
    {
        public Dictionary<string, int> Places { get; init; } = new(StringComparer.Ordinal)
        {
            ["Ukraine"] = 2105,
            ["Yemen"] = 482,
            ["Tigray"] = 163,
        };

        public IReadOnlyDictionary<string, int> PlacesByTheatre => Places;

        public int Ambiguous { get; init; }

        public int AmbiguousNameCount => Ambiguous;
    }
}
