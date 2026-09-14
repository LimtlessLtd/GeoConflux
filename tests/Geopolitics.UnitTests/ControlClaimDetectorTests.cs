using Geopolitics.Application.Control;
using Geopolitics.Domain;

namespace Geopolitics.UnitTests;

/// <summary>
/// Reading a control claim out of ordinary prose, and refusing to.
/// <para>
/// Finding the verbs is easy. The work is refusing every report that uses them about something other
/// than ground — a soldier captured, a shipment seized, an attempt abandoned — because a control
/// claim that reaches the assessment wrongly gets drawn on a map at full confidence, and one that
/// does not reach it merely costs a claim.
/// </para>
/// </summary>
public sealed class ControlClaimDetectorTests
{
    private static readonly ExtractedEntity[] OneActor =
        [new("Northern Coalition Forces", EntityType.Organisation)];

    private static (ControlSignal Signal, string? Actor) Detect(
        string text,
        string place = "Avdiivka",
        ExtractedEntity[]? entities = null) =>
        ControlClaimDetector.Detect(text, place, entities ?? OneActor);

    [Theory]
    [InlineData("Northern Coalition Forces captured Avdiivka on Tuesday.")]
    [InlineData("Northern Coalition Forces recaptured the town of Avdiivka.")]
    [InlineData("Northern Coalition Forces seized the city of Avdiivka overnight.")]
    [InlineData("Northern Coalition Forces took control of Avdiivka.")]
    [InlineData("Northern Coalition Forces overran Avdiivka before dawn.")]
    public void AnActiveClaimThatThePlaceWasTakenIsRead(string text)
    {
        var (signal, actor) = Detect(text);

        Assert.Equal(ControlSignal.TerritoryTransferred, signal);
        Assert.Equal("Northern Coalition Forces", actor);
    }

    [Theory]
    [InlineData("Avdiivka fell to Northern Coalition Forces on Tuesday.")]
    [InlineData("Avdiivka was captured by Northern Coalition Forces.")]
    [InlineData("Avdiivka is now under the control of Northern Coalition Forces.")]
    public void APassiveClaimIsReadToo(string text)
    {
        Assert.Equal(ControlSignal.TerritoryTransferred, Detect(text).Signal);
    }

    [Theory]
    [InlineData("Northern Coalition Forces withdrew from Avdiivka overnight.")]
    [InlineData("Northern Coalition Forces abandoned the town of Avdiivka.")]
    [InlineData("Northern Coalition Forces lost control of Avdiivka.")]
    public void AWithdrawalIsReadAsOne(string text)
    {
        Assert.Equal(ControlSignal.WithdrawalReported, Detect(text).Signal);
    }

    [Fact]
    public void AReportThatIsAboutLeavingIsNotReadAsTakingBecauseItMentionsBoth()
    {
        // "Withdrew from X after recapturing it in March" is a withdrawal report that contains a
        // gain verb. Reading the gain would invert what the report says.
        var (signal, _) = Detect(
            "Northern Coalition Forces withdrew from Avdiivka, which they had recaptured in March.");

        Assert.Equal(ControlSignal.WithdrawalReported, signal);
    }

    [Theory]
    [InlineData("Northern Coalition Forces captured a soldier near Avdiivka.")]
    [InlineData("Northern Coalition Forces seized a weapons shipment bound for Avdiivka.")]
    [InlineData("Northern Coalition Forces abandoned an assault on Avdiivka.")]
    [InlineData("Heavy shelling was reported in Avdiivka overnight.")]
    [InlineData("Northern Coalition Forces captured territory elsewhere; Avdiivka was quiet.")]
    public void AVerbUsedAboutSomethingOtherThanTheGroundIsRefused(string text)
    {
        // Every one of these would fire on a naive proximity rule, and every one of them would put a
        // marker on a map claiming somebody holds a town.
        Assert.Equal(ControlSignal.None, Detect(text).Signal);
    }

    [Fact]
    public void AClaimNamingTwoPartiesIsLeftUndecidedRatherThanAttributedToTheFirst()
    {
        // The case a rule genuinely cannot settle: "A recaptured the town from B" and "B recaptured
        // it from A" differ by word order alone, and guessing would attribute a place to whoever was
        // mentioned first.
        var (signal, actor) = Detect(
            "Northern Coalition Forces recaptured Avdiivka from Southern Militia.",
            entities:
            [
                new ExtractedEntity("Northern Coalition Forces", EntityType.Organisation),
                new ExtractedEntity("Southern Militia", EntityType.Organisation),
            ]);

        Assert.Equal(ControlSignal.None, signal);
        Assert.Null(actor);
    }

    [Fact]
    public void AClaimNamingNobodyAssertsNothing()
    {
        var (signal, actor) = Detect("The town of Avdiivka was captured overnight.", entities: []);

        Assert.Equal(ControlSignal.None, signal);
        Assert.Null(actor);
    }

    [Fact]
    public void AReportAboutAPlaceThisSystemCouldNotPlaceProducesNothing()
    {
        // There would be nowhere to assess it, so reading it would store evidence that could never
        // reach the map and never be checked against one.
        Assert.Equal(ControlSignal.None, ControlClaimDetector.Detect(
            "Northern Coalition Forces captured Avdiivka.", null, OneActor).Signal);

        Assert.Equal(ControlSignal.None, ControlClaimDetector.Detect(null, "Avdiivka", OneActor).Signal);
    }

    [Fact]
    public void APlaceNameContainingRegexMetacharactersIsMatchedLiterally()
    {
        // Place names arrive from a gazetteer and from open feeds. An unescaped one would either
        // throw or match something it should not.
        var (signal, _) = Detect(
            "Northern Coalition Forces captured Sana'a (Old City) at dawn.",
            place: "Sana'a (Old City)");

        Assert.Equal(ControlSignal.TerritoryTransferred, signal);
    }

    [Fact]
    public void OnlyOrganisationsStatesAndUnitsCountAsActors()
    {
        // A person or a vessel named in the text is not a party that can hold ground, and counting
        // one would attribute a town to a journalist.
        var (signal, _) = Detect(
            "Northern Coalition Forces captured Avdiivka, a correspondent reported.",
            entities:
            [
                new ExtractedEntity("Northern Coalition Forces", EntityType.Organisation),
                new ExtractedEntity("A Correspondent", EntityType.Person),
                new ExtractedEntity("Avdiivka", EntityType.Place),
            ]);

        Assert.Equal(ControlSignal.TerritoryTransferred, signal);
    }
}
