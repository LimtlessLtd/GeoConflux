using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// Conflict membership as the shipped pipeline actually records it.
/// <para>
/// These run the real <c>ObservationProcessor</c>, so what they assert is the stage ordering as much
/// as the answer: assignment happens after placement, because where a report was placed is half of
/// what decides where it belongs, and it gates nothing — a report that belongs to no conflict is
/// stored, placed and correlated exactly as before.
/// </para>
/// </summary>
public sealed class ConflictPipelineTests
{
    private static Conflict Ukraine()
    {
        var conflict = Conflict.Coded("ucdp:13243", "Russia - Ukraine", "Government of Russia", "Government of Ukraine");
        conflict.RecordCoded("Pokrovsk raion", "UA", events: 11055, deaths: 75686);
        return conflict;
    }

    private static Conflict Tigray()
    {
        var conflict = Conflict.Coded("ucdp:333", "Ethiopia: Tigray", "Government of Ethiopia", "TPLF");
        conflict.RecordCoded("Mekelle town", "ET", events: 90);
        return conflict;
    }

    private static Conflict Oromiya()
    {
        var conflict = Conflict.Coded("ucdp:413", "Ethiopia: Oromiya", "Government of Ethiopia", "OLA");
        conflict.RecordCoded("Nekemte town", "ET", events: 60);
        return conflict;
    }

    [Fact]
    public async Task ArecordThatStatesItsOwnConflictIsRecordedAgainstThatConflict()
    {
        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(Ukraine(), Tigray()) };
        harness.ResolveAllTo(48.28, 37.17, "Pokrovsk");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope(
                "Shelling reported overnight.",
                sourceIdentifier: "ucdp-1",
                conflictKey: "ucdp:13243"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);
        Assert.Equal("ucdp:13243", Assert.Single(observation.ConflictKeys));
        Assert.Equal(ConflictMatchBasis.Coded, observation.ConflictBasis);
        Assert.Null(observation.ConflictNote);
    }

    [Fact]
    public async Task AreportNamingApartyBelongsToItsConflictEvenWhenItWasPlacedNowhereNearIt()
    {
        var houthis = Conflict.Coded("fixture:yemen", "Yemen", "Government of Yemen", "Houthi movement");
        houthis.RecordCoded("Sanaa city", "YE", events: 500);

        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(houthis) };
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            eventType: EventType.MaritimeIncident,
            confidence: 0.9,
            entities: [new ExtractedEntity("Houthis", EntityType.Organisation)]);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A vessel was struck in the strait.", sourceIdentifier: "wire-1"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        // The strait is in no country and the conflict's coded places are all inland. Actor
        // membership is the only thing that reaches this, which is the case it exists for.
        Assert.Equal("fixture:yemen", Assert.Single(observation.ConflictKeys));
        Assert.Equal(ConflictMatchBasis.Actor, observation.ConflictBasis);
    }

    [Fact]
    public async Task AreportInsideTwoConflictsAndIdentifyingNeitherIsStoredWithBothNamedAndAssignedToNone()
    {
        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(Tigray(), Oromiya()) };
        harness.LocationResolver.Behaviour = _ => new LocationResolution(
            new GeoLocation("Addis Ababa", "ET", 9.03, 38.74),
            LocationResolutionMethod.Gazetteer,
            1.0,
            null,
            null);

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("Fighting was reported.", sourceIdentifier: "wire-2"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        Assert.Empty(observation.ConflictKeys);
        Assert.Null(observation.ConflictBasis);
        Assert.Equal(2, observation.ConflictCandidateKeys.Count);
        Assert.NotNull(observation.ConflictNote);

        // And none of that stopped it being evidence. Membership describes a report; it does not
        // decide whether the report counts.
        Assert.Equal(ObservationStatus.Persisted, observation.Status);
        Assert.NotNull(observation.IncidentId);
    }

    [Fact]
    public async Task AreportNoConflictInTheRegisterReachesIsStillStoredAndSaysWhyItBelongsNowhere()
    {
        var harness = new PipelineTestHarness { Conflicts = new FixedConflictRegister(Ukraine()) };
        harness.ResolveAllTo(-33.87, 151.21, "Sydney");

        await harness.BuildProcessor().ProcessAsync(
            PipelineTestHarness.Envelope("A quiet day.", sourceIdentifier: "wire-3"),
            CancellationToken.None);

        var observation = Assert.Single(harness.Observations.Committed);

        Assert.Empty(observation.ConflictKeys);
        Assert.Empty(observation.ConflictCandidateKeys);
        Assert.Contains("absence of conflict", observation.ConflictNote, StringComparison.Ordinal);
    }
}
