using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Conflicts;
using Xunit.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// The register, and the thing that makes it worth having: it was not written here.
/// </summary>
public sealed class ConflictRegisterTests(ITestOutputHelper output)
{
    private static readonly CodedConflictRegister Register = new();

    [Fact]
    public void TheRegisterHoldsTheConflictsAcodingProjectRecordedRatherThanThreeSomebodyChose()
    {
        // The number that matters is not this one exactly; it is that it is three orders of magnitude
        // away from the three theatres this system used to watch. The floor is a collapse detector in
        // the same spirit as the coverage benchmark's: an extract that stopped loading, or a reader
        // that started discarding rows, would otherwise look like a quiet world.
        Assert.True(
            Register.All.Count > 300,
            $"The register holds {Register.All.Count} conflicts, which is too few to be the extract.");

        Assert.All(Register.All, conflict => Assert.Equal(ConflictOrigin.Coded, conflict.Origin));
        Assert.All(Register.All, conflict => Assert.StartsWith("ucdp:", conflict.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void AconflictsGeographyIsResolvedFromItsOwnCodedPlacesRatherThanDeclared()
    {
        Assert.True(Register.TryGet("ucdp:13243", out var ukraine));

        // UCDP's own place names for this conflict include Russian territory, because that is where
        // some of its events happened. A hand-drawn box around "Ukraine" would have excluded them.
        Assert.Contains("UA", ukraine.Countries);
        Assert.Contains("RU", ukraine.Countries);

        // And the places are held as names, so a report naming one of them is placed in the conflict
        // without any coordinate arithmetic at all.
        Assert.Contains("pokrovsk", ukraine.PlaceKeys);
        Assert.Contains("bakhmut", ukraine.PlaceKeys);
    }

    [Fact]
    public void AcountryBackedByOnePlaceNameThatCollidedIsNotPartOfAconflictsGeography()
    {
        Assert.True(Register.TryGet("ucdp:13243", out var ukraine));

        // Before the two-name rule, this conflict's geography included Turkey, China, Romania and the
        // Philippines, each on the strength of exactly one Ukrainian place name that is spelled like
        // somewhere else. Every report from those four countries would then have been a geographic
        // match for a European war.
        Assert.DoesNotContain("CN", ukraine.Countries);
        Assert.DoesNotContain("PH", ukraine.Countries);
        Assert.DoesNotContain("RO", ukraine.Countries);
        Assert.DoesNotContain("TR", ukraine.Countries);

        // The rule has to leave genuinely wide conflicts wide, or it would have swapped one wrong
        // answer for another. One-sided violence by Islamic State is coded across nine countries and
        // still is.
        Assert.True(Register.TryGet("ucdp:506", out var islamicState));
        Assert.True(
            islamicState.Countries.Count >= 8,
            $"A conflict coded across four continents was narrowed to {islamicState.Countries.Count} countries.");
    }

    [Fact]
    public void OnlyTheWordsThatNameConflictItselfAreDroppedFromEveryEntry()
    {
        // Measured across the register rather than decided: "government" names a party in 117 of the
        // 319 conflicts and "civilians" in 103, and the next commonest word appears in 22. Those two
        // describe what kind of party somebody is rather than which party, and left in, a report
        // naming any government would be assigned to a third of the world's wars.
        Assert.Equal(["civilians", "government"], Register.AmbiguousActorWords);
        Assert.All(Register.All, conflict => Assert.DoesNotContain("government", conflict.ActorTokens));

        // And the distinctive words stay, or actor matching would do nothing at all. "Cartel" appears
        // in nineteen conflicts and survives, because how many words a report shares with each
        // conflict settles that case far better than deleting the word would.
        Assert.True(Register.TryGet("ucdp:13737", out var cartels));
        Assert.Contains("sinaloa", cartels.ActorTokens);
        Assert.Contains("cartel", cartels.ActorTokens);
    }

    [Fact]
    public void TheRegisterReportsWhatItCouldNotPlace()
    {
        var total = Register.ResolvedPlaces + Register.UnresolvedPlaces;

        output.WriteLine(
            $"{Register.All.Count} conflicts, {total} coded place names, "
            + $"{Register.ResolvedPlaces} resolved to a country ({Register.ResolvedPlaces / (double)total:P0}), "
            + $"{Register.AmbiguousActorWords.Count} words dropped as naming conflict rather than a party.");

        foreach (var conflict in Register.All.Take(8))
        {
            output.WriteLine(
                $"  {conflict.Key} {conflict.Name} | {conflict.CodedEvents} events | "
                + $"countries {string.Join('/', conflict.Countries.Order())} | "
                + $"{conflict.PlaceKeys.Count} places | actors {string.Join(' ', conflict.ActorTokens)}");
        }

        // Two thirds of the distinct names do not resolve, and that number is reported rather than
        // buried because it is the ceiling on the whole text path: a conflict whose places this
        // system cannot recognise is a conflict it cannot draw from prose, whoever reports it.
        Assert.True(Register.UnresolvedPlaces > 0, "Every coded place resolved, which is not credible.");
        Assert.True(Register.ResolvedPlaces > total / 4, "The register resolved almost none of its places.");
    }
}

/// <summary>
/// What the membership predicate will and will not assert, which is the whole of the design.
/// <para>
/// The conflicts below are fixtures built to exercise the predicate, not transcriptions of anybody's
/// coding. UCDP's actual party names for Yemen are not these — it codes the Houthi administration as
/// the government and the internationally recognised side as the Presidential Leadership Council,
/// which is a real and awkward fact handled elsewhere. Using the names reporting uses keeps these
/// tests about the predicate rather than about one register's conventions.
/// </para>
/// </summary>
public sealed class ConflictAssignmentTests
{
    private static Conflict Yemen()
    {
        var conflict = Conflict.Coded("ucdp:507", "Yemen (North Yemen)", "Government of Yemen", "Houthi movement");
        conflict.RecordCoded("Sanaa city", "YE", events: 500, deaths: 800);
        conflict.RecordCoded("Hodeidah governorate", "YE");
        return conflict;
    }

    private static Conflict Ethiopia()
    {
        var conflict = Conflict.Coded("ucdp:333", "Ethiopia: Tigray", "Government of Ethiopia", "TPLF");
        conflict.RecordCoded("Mekelle town", "ET", events: 90, deaths: 400);
        return conflict;
    }

    private static Conflict Oromo()
    {
        var conflict = Conflict.Coded("ucdp:418", "Ethiopia: Oromiya", "Government of Ethiopia", "OLF-OLA");
        conflict.RecordCoded("Nekemte town", "ET", events: 60, deaths: 200);
        return conflict;
    }

    [Fact]
    public void AsourcesOwnCodingSettlesMembershipAndNothingElseIsConsulted()
    {
        var yemen = Yemen();

        Assert.Equal(
            ConflictMatchBasis.Coded,
            yemen.Admits(new ConflictCandidate(CodedConflictKey: "ucdp:507")));

        // A record that names a different conflict is not then quietly reassigned to this one because
        // it happens to be in the right country. Overruling the coding with an inference drawn from
        // the coding would be a strange way to respect it.
        Assert.Equal(
            ConflictMatchBasis.None,
            yemen.Admits(new ConflictCandidate(CodedConflictKey: "ucdp:999", CountryCode: "YE", PlaceName: "Sanaa")));
    }

    [Fact]
    public void NamingApartyIsMembershipEvenWhereTheReportWasNeverPlaced()
    {
        var yemen = Yemen();

        // The case actor membership exists for. A strike on shipping is at sea, in no country, and it
        // belongs to this conflict because of who carried it out rather than because of where it was.
        var basis = yemen.Admits(new ConflictCandidate(
            PlaceName: "Bab-el-Mandeb",
            ActorNames: ["Houthis"],
            EventType: EventType.MaritimeIncident));

        Assert.Equal(ConflictMatchBasis.Actor, basis);
    }

    [Fact]
    public void GeographyAloneNarrowsButDoesNotAssertWhenSeveralConflictsShareTheCountry()
    {
        var register = new FixedRegister([Ethiopia(), Oromo()]);
        var assigner = new ConflictAssigner(register);

        var assignment = assigner.Assign(new ConflictCandidate(
            CountryCode: "ET",
            PlaceName: "Addis Ababa",
            EventType: EventType.Conflict));

        Assert.False(assignment.IsAssigned);
        Assert.Equal(2, assignment.Candidates.Count);
        Assert.Contains("not enough", assignment.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeographyAloneDoesAssertWhenItLeavesExactlyOneAnswer()
    {
        var assigner = new ConflictAssigner(new FixedRegister([Ethiopia(), Yemen()]));

        var assignment = assigner.Assign(new ConflictCandidate(
            CountryCode: "ET",
            PlaceName: "Mekelle",
            EventType: EventType.Conflict));

        var membership = Assert.Single(assignment.Memberships);
        Assert.Equal("ucdp:333", membership.ConflictKey);

        // Recorded as a place match rather than promoted to something stronger. Which basis carried
        // the assignment is the part a reader needs in order to weigh it.
        Assert.Equal(ConflictMatchBasis.Place, membership.Basis);
    }

    [Fact]
    public void OneReportCanBelongToMoreThanOneConflict()
    {
        var wider = Conflict.Coded("ucdp:900", "Israel - Iran", "Government of Israel", "Houthi movement");
        wider.RecordCoded("Red Sea", null, events: 40);

        var assigner = new ConflictAssigner(new FixedRegister([Yemen(), wider]));

        var assignment = assigner.Assign(new ConflictCandidate(
            ActorNames: ["Houthi movement"],
            EventType: EventType.MaritimeIncident));

        Assert.Equal(2, assignment.Memberships.Count);
        Assert.All(assignment.Memberships, m => Assert.Equal(ConflictMatchBasis.Actor, m.Basis));
    }

    [Fact]
    public void SomethingThatIsNotAneventInAwarIsNotAdmittedByGeography()
    {
        var yemen = Yemen();

        Assert.Equal(
            ConflictMatchBasis.None,
            yemen.Admits(new ConflictCandidate(CountryCode: "YE", EventType: EventType.Sanctions)));

        Assert.Equal(
            ConflictMatchBasis.None,
            yemen.Admits(new ConflictCandidate(CountryCode: "YE", EventType: EventType.NaturalHazard)));
    }

    [Fact]
    public void AmodelProposedConflictTakesNoMembersUntilSomethingSupportsIt()
    {
        var proposed = Conflict.Proposed("model:jonglei-2026", "Jonglei communal fighting");
        proposed.RecordCoded("Bor town", "SS", events: 3);

        var assigner = new ConflictAssigner(new FixedRegister([proposed]));
        var candidate = new ConflictCandidate(CountryCode: "SS", PlaceName: "Bor", EventType: EventType.Conflict);

        Assert.False(assigner.Assign(candidate).IsAssigned);

        proposed.Corroborate();

        Assert.True(assigner.Assign(candidate).IsAssigned);
    }

    [Fact]
    public void NamingApartySpecificallyBeatsSharingOneWordWithSeveral()
    {
        var jalisco = Conflict.Coded("ucdp:13737", "Jalisco Cartel New Generation - Sinaloa Cartel",
            "Jalisco Cartel New Generation", "Sinaloa Cartel");
        jalisco.RecordCoded("Culiacan city", "MX", events: 543);

        var santaRosa = Conflict.Coded("ucdp:14532", "Jalisco Cartel New Generation - Santa Rosa de Lima Cartel",
            "Jalisco Cartel New Generation", "Santa Rosa de Lima Cartel");
        santaRosa.RecordCoded("Celaya city", "MX", events: 761);

        var comando = Conflict.Coded("ucdp:13972", "Comando Vermelho - PCC", "Comando Vermelho", "PCC");
        comando.RecordCoded("Rio de Janeiro city", "BR", events: 508);

        var assigner = new ConflictAssigner(new FixedRegister([jalisco, santaRosa, comando]));

        // All three conflicts here are cartel wars and two of them are fought by the same cartel, so
        // "Jalisco" and "Cartel" do not separate anything. Naming Sinaloa does, and the assignment
        // follows the words the report actually used rather than the ones it happened to share.
        var assignment = assigner.Assign(new ConflictCandidate(
            ActorNames: ["Jalisco Cartel New Generation", "Sinaloa Cartel"],
            EventType: EventType.Conflict));

        var membership = Assert.Single(assignment.Memberships);
        Assert.Equal("ucdp:13737", membership.ConflictKey);

        // The runner-up is not discarded. It shares four words with the report and is offered as a
        // candidate, which is a different statement from "this does not belong there".
        Assert.Contains(assignment.Candidates, m => m.ConflictKey == "ucdp:14532");
    }

    [Fact]
    public void AcountryNameSharedByEveryConflictInItIdentifiesNoneOfThem()
    {
        // Found by running the real pipeline rather than by reasoning about it. Reports about a
        // vessel struck off Qeshm Island were assigned to four Iranian conflicts at once — Iran
        // against Israel, Iran against Islamic State, and two more — because UCDP writes its state
        // parties as "Government of Iran" and every one of those lists therefore contains the word
        // "Iran". That word is the name of the country, not of anybody fighting.
        var conflicts = new[]
        {
            Conflict.Coded("ucdp:14609", "Iran - Israel", "Government of Iran", "Government of Israel"),
            Conflict.Coded("ucdp:338", "Iran: Government", "Government of Iran", "Jaish al-Adl"),
            Conflict.Coded("ucdp:14268", "Iran: Islamic State", "Government of Iran", "IS"),
            Conflict.Coded("ucdp:709", "Government of Iran - Civilians", "Government of Iran", "Civilians"),
        };

        foreach (var conflict in conflicts)
        {
            conflict.RecordCoded("Tehran city", "IR", events: 20);
        }

        var assigner = new ConflictAssigner(new FixedRegister(conflicts));

        var assignment = assigner.Assign(new ConflictCandidate(
            ActorNames: ["Iran", "Iranian navy"],
            EventType: EventType.MaritimeIncident));

        Assert.False(assignment.IsAssigned);
        Assert.Equal(4, assignment.Candidates.Count);
        Assert.Contains("nothing that separates them", assignment.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoWordsSharedWithSeveralConflictsIsStillAnIdentification()
    {
        // The other half of the rule, and the case it must not break. One movement that is a party
        // to two wars is exactly the multi-membership this design exists to express, and it is told
        // apart from the case above by how much of the name the report actually used.
        var yemen = Conflict.Coded("fixture:yemen", "Yemen", "Government of Yemen", "Houthi movement");
        yemen.RecordCoded("Sanaa city", "YE", events: 500);

        var wider = Conflict.Coded("fixture:wider", "Israel - Iran", "Government of Israel", "Houthi movement");
        wider.RecordCoded("Red Sea", null, events: 40);

        var assignment = new ConflictAssigner(new FixedRegister([yemen, wider]))
            .Assign(new ConflictCandidate(
                ActorNames: ["Houthi movement"],
                EventType: EventType.MaritimeIncident));

        Assert.Equal(2, assignment.Memberships.Count);
    }

    [Fact]
    public void AplaceNameMatchesOnlyWhereTheCountryAgrees()
    {
        var lebanon = Conflict.Coded("ucdp:426", "Israel: Southern Lebanon", "Government of Israel", "Hezbollah");
        lebanon.RecordCoded("Tyre city", "LB", events: 1132);

        // Place names collide worldwide, and a register assembled from coded place names holds the
        // collisions too. Without the country check, a report from a same-named town half a world
        // away is a place-level match — the strongest claim geography can make — for a war it has
        // nothing to do with.
        Assert.Equal(
            ConflictMatchBasis.None,
            lebanon.Admits(new ConflictCandidate(CountryCode: "BR", PlaceName: "Tyre", EventType: EventType.Conflict)));

        Assert.Equal(
            ConflictMatchBasis.Place,
            lebanon.Admits(new ConflictCandidate(CountryCode: "LB", PlaceName: "Tyre", EventType: EventType.Conflict)));
    }

    [Fact]
    public void AnUnplacedReportNamingNobodyIsLeftUnassignedAndSaysWhy()
    {
        var assignment = new ConflictAssigner(new FixedRegister([Yemen()]))
            .Assign(new ConflictCandidate(EventType: EventType.Conflict));

        Assert.False(assignment.IsAssigned);
        Assert.Empty(assignment.Candidates);
        Assert.Contains("guessing", assignment.Note, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedRegister(IReadOnlyList<Conflict> conflicts)
        : Geopolitics.Application.Abstractions.IConflictRegister
    {
        public IReadOnlyList<Conflict> All { get; } = conflicts;

        public string Provenance => "a fixture";

        public IReadOnlyList<string> AmbiguousActorWords { get; } = ConflictActorIndex.Prune(conflicts);

        public bool TryGet(string? key, out Conflict conflict)
        {
            conflict = All.FirstOrDefault(c => c.Key == key)!;
            return conflict is not null;
        }
    }
}
