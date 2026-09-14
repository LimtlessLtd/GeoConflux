using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Control;
using Geopolitics.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// Assessing who holds a place, and refusing to.
/// <para>
/// Three of the four possible answers are refusals or qualifications, and they are what makes this
/// defensible rather than a worse ISW. An assessment that carries its evidence and states its own
/// age is checkable; an analyst's polygon is authoritative because of who drew it and is not.
/// </para>
/// </summary>
public sealed class ControlAssessmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneCodedTransferAssertsOnItsOwn()
    {
        // Tier A is the only basis that asserts alone, and the reason is that it is not this system
        // assessing: a named organisation's coder made this call against published criteria.
        var report = await Assess(Coded("Invented State Forces", Now.AddDays(-2)));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Assessed, place.Verdict);
        Assert.Equal("Invented State Forces", place.Actor);
        Assert.Equal(1, report.PlacesAssessed);
    }

    [Fact]
    public async Task OneUncorroboratedClaimAssertsNothing()
    {
        // The corroboration gate's argument, applied to control. One channel saying a town has
        // fallen is evidence that a channel said so.
        var report = await Assess(Claimed("Invented Armed Group", Now.AddDays(-1), "telegram"));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Insufficient, place.Verdict);
        Assert.Null(place.Actor);
        Assert.Contains("evidence that somebody said so", place.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoIndependentClaimsReachAnAssertion()
    {
        var report = await Assess(
            Claimed("Invented Armed Group", Now.AddDays(-1), "telegram"),
            Claimed("Invented Armed Group", Now.AddDays(-1), "bluesky"));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Assessed, place.Verdict);
        Assert.Equal(2, place.SourceCount);
    }

    [Fact]
    public async Task TwoClaimsFromOneSourceDoNotCorroborateThemselves()
    {
        // Two posts on one channel is one channel. Counting them as two would let a single source
        // corroborate itself, which is the failure the gate exists to prevent.
        var report = await Assess(
            Claimed("Invented Armed Group", Now.AddDays(-1), "telegram"),
            Claimed("Invented Armed Group", Now.AddDays(-2), "telegram"));

        Assert.Equal(ControlVerdict.Insufficient, Assert.Single(report.Places).Verdict);
    }

    [Fact]
    public async Task TwoActorsInsideTheContestWindowAreBothShownAndNeitherIsPreferred()
    {
        // Averaging them, or taking the larger pile, would manufacture a consensus that does not
        // exist. That is the corroboration gate's argument applied to geometry.
        var report = await Assess(
            Coded("Invented State Forces", Now.AddDays(-2)),
            Coded("Invented Armed Group", Now.AddDays(-9)));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Contested, place.Verdict);
        Assert.Null(place.Actor);
        Assert.Contains("Invented Armed Group and Invented State Forces", place.Statement, StringComparison.Ordinal);
        Assert.Contains("neither is preferred", place.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoActorsFarApartInTimeMeanThePlaceChangedHandsRatherThanThatItIsContested()
    {
        // A transfer in January and another in March is a place that changed hands, and the later
        // one is the answer. Control is a time series, not a state.
        var report = await Assess(
            Coded("Invented State Forces", Now.AddDays(-3)),
            Coded("Invented Armed Group", Now.AddDays(-60)));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Assessed, place.Verdict);
        Assert.Equal("Invented State Forces", place.Actor);
    }

    [Fact]
    public async Task EvidencePastTheStalenessHorizonReadsAsLastAssertedRatherThanAsHeld()
    {
        // ISW re-assesses daily. A pipeline that cannot must say how far behind it is rather than
        // let an old assessment keep the appearance of a current one.
        var report = await Assess(Coded("Invented State Forces", Now.AddDays(-45)));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Stale, place.Verdict);
        Assert.Equal(45, place.AgeDays);
        Assert.Contains("last asserted", place.Statement, StringComparison.Ordinal);
        Assert.Contains("silence is not evidence", place.Statement, StringComparison.Ordinal);

        // Still not counted as an assertion in the headline figures.
        Assert.Equal(0, report.PlacesAssessed);
    }

    [Fact]
    public async Task AWithdrawalAloneAssertsNobody()
    {
        // The only signal that can end an assessment without another actor making one. Without it a
        // picture could only ever gain control claims and would drift towards whoever was reported
        // first, permanently.
        var report = await Assess(
            Signal("Invented State Forces", Now.AddDays(-1), ControlSignal.WithdrawalReported, ControlEvidenceBasis.Coded, "acled"));

        var place = Assert.Single(report.Places);
        Assert.Equal(ControlVerdict.Insufficient, place.Verdict);
        Assert.Null(place.Actor);
        Assert.Contains("nothing asserting who holds it now", place.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryAssessmentCarriesTheRecordsBehindItRatherThanACount()
    {
        // The whole product argument. If a reader cannot reach the evidence, it is not an
        // assessment — it is a guess wearing one's clothes.
        var first = Coded("Invented State Forces", Now.AddDays(-2));
        var second = Coded("Invented State Forces", Now.AddDays(-4));

        var place = Assert.Single((await Assess(first, second)).Places);

        Assert.Equal(2, place.Evidence.Count);
        Assert.Equal([first.ObservationId, second.ObservationId], place.Evidence.Select(e => e.ObservationId));
    }

    [Fact]
    public async Task PlacesSharingANameInDifferentCountriesAreNotMerged()
    {
        // The gazetteer holds a great many places sharing a name. Merging two of them would assess
        // one war's evidence onto another continent.
        var report = await Assess(
            Coded("Invented State Forces", Now.AddDays(-2)) with { CountryCode = "UA" },
            Coded("Invented Other Forces", Now.AddDays(-2)) with { CountryCode = "SD" });

        Assert.Equal(2, report.Places.Count);
        Assert.All(report.Places, place => Assert.Equal(ControlVerdict.Assessed, place.Verdict));
    }

    [Fact]
    public async Task NoEvidenceAtAllIsStatedAsAGapInReachRatherThanAsPeace()
    {
        // The case that ships. A clone with no dataset credential polls nothing that codes
        // territorial change, so it assesses nothing — and a map that filled itself in would be
        // the failure this whole project exists to avoid.
        var report = await Assess();

        Assert.Empty(report.Places);
        Assert.Contains("not about the world", report.Note, StringComparison.Ordinal);
        Assert.Contains("dataset credential", report.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMethodTravelsWithThePayloadAndSaysItIsNotAFrontLine()
    {
        var report = await Assess(Coded("Invented State Forces", Now.AddDays(-2)));

        Assert.Contains("never from reports of fighting", report.Method, StringComparison.Ordinal);
        Assert.Contains("not a front line", report.Method, StringComparison.Ordinal);
    }

    private static ControlObservation Coded(string actor, DateTimeOffset at) =>
        Signal(actor, at, ControlSignal.TerritoryTransferred, ControlEvidenceBasis.Coded, "acled");

    private static ControlObservation Claimed(string actor, DateTimeOffset at, string source) =>
        Signal(actor, at, ControlSignal.TerritoryTransferred, ControlEvidenceBasis.Claimed, source);

    private static ControlObservation Signal(
        string actor,
        DateTimeOffset at,
        ControlSignal signal,
        ControlEvidenceBasis basis,
        string source) =>
        new(Guid.NewGuid(), source, at, signal, basis, actor, "Invented Town", "UA", 49.0, 36.0, LocationPrecision.Settlement);

    private static async Task<ControlReport> Assess(params ControlObservation[] signals)
    {
        var service = new ControlAssessmentService(
            new StubRepository(signals),
            Options.Create(new ControlOptions()),
            new FakeTimeProvider(Now));

        return await service.BuildAsync(CancellationToken.None);
    }

    private sealed class StubRepository(IReadOnlyList<ControlObservation> signals) : IControlRepository
    {
        public Task<IReadOnlyList<ControlObservation>> ListSignalsAsync(
            DateTimeOffset since,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ControlObservation>>(
                [.. signals.Where(signal => signal.OccurredAt >= since)]);
    }
}
