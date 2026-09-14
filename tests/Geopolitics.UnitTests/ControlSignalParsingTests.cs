using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;

namespace Geopolitics.UnitTests;

/// <summary>
/// Reading the one thing in an ACLED record that is about <em>control</em> rather than about an
/// event.
/// <para>
/// Three of ACLED's sub-event types are a coder asserting that a place changed hands. Until
/// [ADR 037] the parser read the field, used it to build a headline, and threw the distinction away
/// — so the strongest evidence about control anywhere in this system was arriving and being
/// discarded, which is the same defect Sprint 16 found with <c>conflict_name</c>.
/// </para>
/// <para>
/// The negative cases are the ones that matter. An armed clash must produce no control signal, and a
/// transfer naming nobody must produce none either.
/// </para>
/// </summary>
public sealed class ControlSignalParsingTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    private static AcledEvent Event(string identifier) =>
        AcledResponseParser.Parse(Fixture("acled-territorial.json"))
            .Single(value => value.Identifier == identifier);

    [Theory]
    [InlineData("UKR99010", "Invented State Forces")]
    [InlineData("UKR99011", "Invented Armed Group")]
    [InlineData("UKR99012", "Invented State Forces")]
    public void ACodedTerritorialTransferIsReadAsOneAndNamesWhoTookIt(string identifier, string actor)
    {
        var record = Event(identifier);

        Assert.Equal(ControlSignal.TerritoryTransferred, record.ControlSignal);

        // The party the coding names first, which for every one of these sub-types is the one
        // gaining control. Kept as the coding's own wording, because that is the only text a reader
        // could check the assertion against.
        Assert.Equal(actor, record.ControlActor);
    }

    [Fact]
    public void ThreeSubTypesCountAsATransferAndTheyAreNotAllUnderTheSameEventType()
    {
        // The two contested transfers are coded under Battles and the uncontested one under
        // Strategic developments, which is why the mapping matches on the sub-type alone.
        var transfers = AcledResponseParser.Parse(Fixture("acled-territorial.json"))
            .Where(record => record.ControlSignal == ControlSignal.TerritoryTransferred)
            .ToList();

        Assert.Equal(3, transfers.Count);
    }

    [Fact]
    public void AnArmedClashCarriesNoControlSignalAtAll()
    {
        // The whole distinction the design turns on. An actor fighting at a place is evidence about
        // that place and is not a claim to hold it — and event density says where fighting was
        // reported, which is a function of where journalists and coders were as much as where
        // soldiers were.
        var record = Event("UKR99013");

        Assert.Equal(ControlSignal.None, record.ControlSignal);
        Assert.Null(record.ControlActor);
    }

    [Fact]
    public void ATransferNamingNobodyIsDroppedRatherThanStoredUnattributed()
    {
        // "Territory changed hands" without naming who took it is evidence of control by nobody.
        // Storing it would put a row into the assessment that could never support an assertion and
        // could never be checked.
        var record = Event("UKR99014");

        Assert.Equal(ControlSignal.None, record.ControlSignal);
        Assert.Null(record.ControlActor);
    }

    [Fact]
    public void TheOrdinaryFixtureStillProducesNoControlSignals()
    {
        // The regression guard for the mapping being too eager. A protest, which is what the
        // standing fixture holds, says nothing about who holds anything.
        Assert.All(
            AcledResponseParser.Parse(Fixture("acled-response.json")),
            record => Assert.Equal(ControlSignal.None, record.ControlSignal));
    }

    [Fact]
    public void ARecordedTransferSurvivesIntoTheObservationWithItsBasisStated()
    {
        // The domain refuses a signal it cannot attribute, and records where the signal came from —
        // which is what decides the weight it can carry downstream.
        var observation = new RawObservation(
            Guid.NewGuid(),
            ObservationKind.ExternalEvent,
            "acled",
            "An invented recapture recorded for fixture purposes.",
            "UKR99010",
            DateTimeOffset.UtcNow,
            ObservationProvenance.Polled);

        observation.RecordControlSignal(
            ControlSignal.TerritoryTransferred, "Invented State Forces", ControlEvidenceBasis.Coded);

        Assert.True(observation.CarriesControlSignal);
        Assert.Equal(ControlEvidenceBasis.Coded, observation.ControlBasis);

        Assert.Throws<DomainException>(() =>
            observation.RecordControlSignal(ControlSignal.TerritoryTransferred, "  ", ControlEvidenceBasis.Coded));
    }
}
