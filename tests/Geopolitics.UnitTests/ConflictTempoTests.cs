using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// What a change in reporting volume is, and is not, evidence of.
/// <para>
/// The case that decides the design is the last one. A conflict whose coverage thins and whose
/// reporting falls with it produces exactly the same numbers as a conflict that has gone quiet, and a
/// line drawn through them shows a de-escalation at what may be the moment of escalation. This
/// repository already records a real instance: ACLED's Ethiopia Peace Observatory ended fortnightly
/// updates on 1 July 2025, about six months before fighting resumed in January 2026.
/// </para>
/// </summary>
public sealed class ConflictTempoTests
{
    private static ConflictTempo Assess(
        (int Reports, int Sources) current,
        (int Reports, int Sources) previous) =>
        ConflictTempoAssessment.Assess(
            "ucdp:333",
            "Ethiopia: Tigray",
            new ConflictWindowCounts(current.Reports, current.Sources, 0),
            new ConflictWindowCounts(previous.Reports, previous.Sources, 0));

    [Fact]
    public void WhenReportsAndSourcesFallTogetherTheTempoIsNotStated()
    {
        var tempo = Assess(current: (9, 1), previous: (40, 5));

        Assert.Equal(TempoVerdict.NotStated, tempo.Verdict);
        Assert.Contains("Coverage changed; tempo cannot be stated", tempo.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheReportingBaseHeldAfallIsAfall()
    {
        // Same drop in reports, same number of sources still watching. Now the number means what it
        // appears to mean, and saying so is the other half of being careful — a system that refuses
        // to state anything is as useless as one that overstates everything.
        var tempo = Assess(current: (9, 5), previous: (40, 5));

        Assert.Equal(TempoVerdict.Falling, tempo.Verdict);
        Assert.Contains("reporting base held", tempo.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreSourcesReportingAsMuchEachIsWiderCoverageRatherThanMoreFighting()
    {
        // Reports doubled. So did the sources, and each one is saying about what it said before.
        var tempo = Assess(current: (40, 8), previous: (20, 4));

        Assert.Equal(TempoVerdict.NotStated, tempo.Verdict);
        Assert.Contains("more coverage, not evidently more fighting", tempo.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreSourcesEachReportingMoreIsArise()
    {
        var tempo = Assess(current: (80, 6), previous: (20, 4));

        Assert.Equal(TempoVerdict.Rising, tempo.Verdict);
        Assert.Contains("not only wider coverage", tempo.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void AthinConflictSaysSoRatherThanShowingAdirection()
    {
        // Three against one is a two hundred per cent rise and it is also two people writing about
        // the same week. Showing it as a direction would give the least-covered conflicts the
        // loudest movements, which is precisely backwards.
        var tempo = Assess(current: (3, 1), previous: (1, 1));

        Assert.Equal(TempoVerdict.TooThin, tempo.Verdict);
        Assert.Contains("Too few either way", tempo.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void AconflictWithNoPreviousWindowHasNoBaselineRatherThanAninfiniteRise()
    {
        var tempo = Assess(current: (12, 3), previous: (0, 0));

        Assert.Equal(TempoVerdict.NoBaseline, tempo.Verdict);
        Assert.Contains("nothing to compare", tempo.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void SteadyIsStated()
    {
        var tempo = Assess(current: (21, 4), previous: (20, 4));

        Assert.Equal(TempoVerdict.Steady, tempo.Verdict);
        Assert.Contains("No material change", tempo.Statement, StringComparison.Ordinal);
    }
}

/// <summary>The report as assembled, including the counts that deliberately do not sum.</summary>
public sealed class ConflictActivityServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static Conflict Tigray()
    {
        var conflict = Conflict.Coded("ucdp:333", "Ethiopia: Tigray");
        conflict.RecordCoded("Mekelle town", "ET", events: 90);
        return conflict;
    }

    private static Conflict Yemen()
    {
        var conflict = Conflict.Coded("fixture:yemen", "Yemen");
        conflict.RecordCoded("Sanaa city", "YE", events: 500);
        return conflict;
    }

    [Fact]
    public async Task AreportBelongingToTwoConflictsIsCountedInBothAndTheReportSaysTheRowsDoNotSum()
    {
        var repository = new StubConflictActivityRepository
        {
            Current =
            [
                new ConflictObservationSample(["ucdp:333", "fixture:yemen"], "reuters", Guid.NewGuid(), 0),
                new ConflictObservationSample(["ucdp:333"], "afp", Guid.NewGuid(), 0),
            ],
        };

        var report = await new ConflictActivityService(
            repository,
            new FixedConflictRegister(Tigray(), Yemen()),
            new FakeTimeProvider(Now)).BuildAsync(AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.Equal(2, report.Conflicts.Count);
        Assert.Equal(2, report.Conflicts.Single(c => c.Conflict == "ucdp:333").Current.Observations);
        Assert.Equal(1, report.Conflicts.Single(c => c.Conflict == "fixture:yemen").Current.Observations);

        // Three rows of counts over two reports. Correct, and it has to be said out loud or a reader
        // will add them up.
        Assert.Contains("do not sum", report.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCountCarriesHowManySourcesProducedIt()
    {
        var repository = new StubConflictActivityRepository
        {
            Current =
            [
                new ConflictObservationSample(["ucdp:333"], "reuters", null, 0),
                new ConflictObservationSample(["ucdp:333"], "reuters", null, 0),
                new ConflictObservationSample(["ucdp:333"], "afp", null, 0),
            ],
        };

        var report = await new ConflictActivityService(
            repository,
            new FixedConflictRegister(Tigray()),
            new FakeTimeProvider(Now)).BuildAsync(AnalyticsWindow.Last7Days, CancellationToken.None);

        var tempo = Assert.Single(report.Conflicts);
        Assert.Equal(3, tempo.Current.Observations);
        Assert.Equal(2, tempo.Current.Sources);
    }

    [Fact]
    public async Task NothingCoversThisIsCountedApartFromNothingSaidWhich()
    {
        var repository = new StubConflictActivityRepository
        {
            Current =
            [
                new ConflictObservationSample([], "reuters", null, CandidateCount: 0),
                new ConflictObservationSample([], "afp", null, CandidateCount: 3),
            ],
        };

        var report = await new ConflictActivityService(
            repository,
            new FixedConflictRegister(Tigray()),
            new FakeTimeProvider(Now)).BuildAsync(AnalyticsWindow.Last7Days, CancellationToken.None);

        // Two unassigned reports, and they say opposite things about the register. One is a gap in
        // what is catalogued; the other is a gap in what the report said about itself.
        Assert.Equal(1, report.Unassigned);
        Assert.Equal(1, report.Undecided);
    }

    [Fact]
    public async Task HowManyOfTheRegistersConflictsWereSeenAtAllIsReported()
    {
        var repository = new StubConflictActivityRepository
        {
            Current = [new ConflictObservationSample(["ucdp:333"], "reuters", null, 0)],
        };

        var report = await new ConflictActivityService(
            repository,
            new FixedConflictRegister(Tigray(), Yemen()),
            new FakeTimeProvider(Now)).BuildAsync(AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.Equal(2, report.Registered);
        Assert.Equal(1, report.Seen);
    }

    private sealed class StubConflictActivityRepository : IConflictActivityRepository
    {
        public IReadOnlyList<ConflictObservationSample> Current { get; init; } = [];

        public IReadOnlyList<ConflictObservationSample> Previous { get; init; } = [];

        private bool asked;

        public Task<ConflictActivitySample> SampleAsync(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int take,
            CancellationToken cancellationToken)
        {
            // The service asks for the current window first and the baseline second, which is the
            // order that matters and the order this depends on.
            var sample = asked ? Previous : Current;
            asked = true;
            return Task.FromResult(new ConflictActivitySample(sample, false));
        }

        public IReadOnlyList<ConflictEvidence> Evidence { get; init; } = [];

        public Task<IReadOnlyList<ConflictEvidence>> EvidenceAsync(
            string conflictKey,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConflictEvidence>>([.. Evidence.Take(take)]);
    }
}
