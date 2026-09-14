using Geopolitics.Infrastructure.Location;
using Xunit.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// Global placement, asserted where the sprint said it would be visible: <em>a report naming a
/// district town in a country this project has never touched is drawn in the right place.</em>
/// <para>
/// Before this the lexicon held 2,750 places across three theatres plus about forty worldwide
/// chokepoints and country centroids. A clash in Kayin State, Kidal or Bajo Cauca was stored,
/// classified, deduplicated and scored, and then had nowhere to go on the map. Everything else in
/// the global coverage assessment was downstream of that, which is why this is the sprint that had
/// to come first.
/// </para>
/// </summary>
public sealed class GlobalGazetteerTests(ITestOutputHelper output)
{
    /// <summary>
    /// Places in countries nothing here has ever been pointed at, with coordinates asserted to a
    /// tenth of a degree — about eleven kilometres, tight enough to catch a wrong town and loose
    /// enough to survive a revised centroid.
    /// </summary>
    [Theory]
    [InlineData("Kayin State", 17.50, 97.75)]
    [InlineData("Hpa-An", 16.87, 97.72)]
    [InlineData("Kidal", 19.40, 1.20)]
    [InlineData("Bukavu", -2.50, 28.87)]
    [InlineData("Kismayo", -0.36, 42.55)]
    public void APlaceInACountryNobodyTaskedResolvesToItself(string name, double latitude, double longitude)
    {
        Assert.True(Gazetteer.TryResolve(name, out var entry), $"'{name}' did not resolve.");

        output.WriteLine($"{name} -> {entry.CanonicalName} ({entry.CountryCode}) {entry.Precision}");

        // A tenth of a degree is about eleven kilometres: tight enough to catch a wrong town, loose
        // enough to survive a revised centroid. Asserted as a distance rather than by rounding to a
        // decimal place, because a coordinate landing exactly on a midpoint rounds away from its own
        // expected value.
        Assert.True(
            Math.Abs(latitude - entry.Latitude) <= 0.1 && Math.Abs(longitude - entry.Longitude) <= 0.1,
            $"{name} resolved to {entry.Latitude:F3}, {entry.Longitude:F3} rather than {latitude}, {longitude}.");
    }

    [Fact]
    public void TheCoarseLayerPlacesANamedDistrictAndDoesNotGuessOneOutOfProse()
    {
        // The two entry points, and the line between them is where this sprint spent most of its
        // argument. Everything that names a location — a coded dataset, an enrichment provider, a
        // submission — arrives through TryResolve, and gets all 78,547 places.
        Assert.True(Gazetteer.TryResolve("Hpa-An", out var town));
        Assert.Equal("MM", town.CountryCode);

        // Hunting the same name out of raw prose is a guess, and at this scale a measured bad one:
        // scanning this repository's own corpora with the coarse layer in the scan fired 38 names,
        // nearly all ordinary English words that are also administrative units somewhere.
        Assert.Null(Gazetteer.FindFirstMention(
            "Clashes were reported in Hpa-An early on Tuesday, according to local media."));

        // Which is what keeps the ordinary words out. Every one of these is a real place.
        foreach (var sentence in new[]
        {
            "Exchange of detainees completed on Tuesday.",
            "Patrol reports a quiet night along the contact line.",
            "The gathering dispersed by early evening and police reported no arrests.",
            "Delegations meet for scheduled maritime boundary talks.",
            "Shelling damages buildings in a border village.",
        })
        {
            Assert.Null(Gazetteer.FindFirstMention(sentence));
        }
    }

    [Fact]
    public void ADeliberateLayerIsStillHuntedForInProse()
    {
        // The other half: what somebody chose is still found in running text, which is what the
        // offline provider reads. Narrowing the scan to the chosen layers cost none of this.
        Assert.Equal("Bab-el-Mandeb", Gazetteer.FindFirstMention(
            "A cargo ship transiting the Bab-el-Mandeb strait reported small craft approaching."));

        Assert.Equal("Pokrovsk", Gazetteer.FindFirstMention(
            "Heavy fighting was reported around Pokrovsk through the night."));

        Assert.Equal("Marib Governorate", Gazetteer.FindFirstMention(
            "An airstrike was reported in Marib on Tuesday."));
    }

    [Fact]
    public void APlaceIsFoundByTheNameItsOwnCountryUsesForIt()
    {
        // A conflict is reported in the language it happens in, and a Latin-only lexicon resolves
        // none of that. This is most of what the extract's size is spent on.
        Assert.True(Gazetteer.TryResolve("ကရင်ပြည်နယ်", out var burmese));
        Assert.Equal("MM", burmese.CountryCode);

        Assert.True(Gazetteer.TryResolve("ولاية كايين", out var arabic));
        Assert.Equal("MM", arabic.CountryCode);
    }

    /// <summary>
    /// The three theatres that were bought deliberately must not be coarsened by the layer added
    /// underneath them. Each of these shares its name with the administrative unit around it, and the
    /// nested rule would happily resolve all of them to a district centroid.
    /// </summary>
    [Theory]
    [InlineData("Pokrovsk")]
    [InlineData("Bakhmut")]
    [InlineData("Kramatorsk")]
    [InlineData("Mekele")]
    public void ATaskedTheatreKeepsItsSettlementPrecision(string name)
    {
        Assert.True(Gazetteer.TryResolve(name, out var entry), $"'{name}' did not resolve.");
        Assert.Equal(PlacePrecision.Settlement, entry.Precision);
    }

    /// <summary>
    /// The correction the global layer forced. A tasked theatre holding a name outright meant a
    /// Ukrainian village of 9,917 held "New York" and a hamlet of 1,042 held "Victoria" — a marker
    /// placed in the wrong country at full confidence, which ADR 026 is explicit is worse than no
    /// marker at all.
    /// </summary>
    [Theory]
    [InlineData("New York", "US")]
    [InlineData("Cairo", "EG")]

    // Alexandria is the sharpest of these: the tasked layer holds Oleksandriia, a Ukrainian town of
    // 76,097 whose recorded spelling is "Alexandria". Egypt's Alexandria is 5.6 million.
    [InlineData("Alexandria", "EG")]
    public void AFarLargerPlaceElsewhereTakesANameFromATaskedVillage(string name, string country)
    {
        Assert.True(Gazetteer.TryResolve(name, out var entry), $"'{name}' did not resolve.");

        output.WriteLine($"{name} -> {entry.CanonicalName} ({entry.CountryCode})");

        Assert.Equal(country, entry.CountryCode);
    }

    [Fact]
    public void ANameNoRuleCanSettleResolvesToNothingWithoutContext()
    {
        // Unchanged behaviour, and the point of the refusal. There is no answer to "Victoria" that is
        // right often enough to assert, so nothing is asserted.
        Assert.False(Gazetteer.TryResolve("Victoria", out _));
        Assert.False(Gazetteer.TryResolve("Victoria", new PlaceContext(), out _));
    }

    [Fact]
    public void TheCountryAReportStatesSettlesAContestedName()
    {
        Assert.True(Gazetteer.TryResolve("Victoria", new PlaceContext("UA"), out var ukrainian));
        Assert.Equal("UA", ukrainian.CountryCode);

        Assert.True(Gazetteer.TryResolve("San Jose", new PlaceContext("US"), out var american));
        Assert.Equal("US", american.CountryCode);
    }

    [Fact]
    public void ThePlacesAReportNamesAlongsideItSettleAContestedName()
    {
        // The second signal, and the one that works when no provider stated a country: the other
        // places in the same text say which country is being written about.
        var context = new PlaceContext(
            Text: "Fighting was reported near Kramatorsk and Sloviansk. A separate incident was "
                + "recorded at Victoria the same evening.");

        Assert.True(Gazetteer.TryResolve("Victoria", context, out var entry));
        Assert.Equal("UA", entry.CountryCode);
    }

    [Fact]
    public void ContextNarrowsAContestedNameAndCannotMoveASettledOne()
    {
        // The boundary that keeps this on the right side of ADR 005. Context chooses between places
        // the lexicon already holds; it never invents, moves, or overrules one that was not in doubt.
        Assert.True(Gazetteer.TryResolve("Pokrovsk", new PlaceContext("EG"), out var pokrovsk));
        Assert.Equal("UA", pokrovsk.CountryCode);

        Assert.True(Gazetteer.TryResolve("Kyiv", new PlaceContext("MM"), out var kyiv));
        Assert.Equal("UA", kyiv.CountryCode);
    }

    [Fact]
    public void AContextNamingACountryTheLexiconCannotSettleStillRefuses()
    {
        // Narrowing is not the same as answering. A country holding two genuinely different places
        // with one name is exactly what context cannot resolve, and picking the larger would be the
        // confident misplacement the refusal exists to prevent.
        Assert.False(Gazetteer.TryResolve("Victoria", new PlaceContext("ZZ"), out _));
        Assert.False(Gazetteer.TryResolve("Victoria", new PlaceContext(Text: "no places are named here"), out _));
    }

    [Fact]
    public void AShortOrdinaryWordFromTheCoarseLayerIsNotHuntedForInProse()
    {
        // The failure this sprint had to fix twice. "Of" is a district of Trabzon with 31,951
        // inhabitants and "شحن" is a district of Al Mahrah; both are also ordinary words, and both
        // outranked every real place name in the sentences that contained them.
        Assert.Equal("Black Sea", Gazetteer.FindFirstMention(
            "A vessel was stopped north of the Black Sea shipping lane."));

        Assert.Equal("Bab-el-Mandeb", Gazetteer.FindFirstMention(
            "أفادت تقارير بأن سفينة شحن تعرضت لاقتراب قوارب صغيرة قرب باب المندب، ولم تقع إصابات."));
    }

    [Fact]
    public void AShortNameFromTheCoarseLayerStillResolvesWhenACallerNamesIt()
    {
        // The two entry points ask different questions, and this is the half that is unchanged: a
        // caller passing "Of" has asserted it is a place name, and a structured provider naming a
        // district in its own location field is exactly that caller.
        Assert.True(Gazetteer.TryResolve("Of", out var trabzon));
        Assert.Equal("TR", trabzon.CountryCode);
    }

    [Fact]
    public void TheLexiconReportsItsOwnCeilingPerCountry()
    {
        var lexicon = new GazetteerPlaceLexicon();

        // The figure the coverage panel publishes. It is a limit on this system rather than a fact
        // about a country, and it is wildly uneven — which is the reason to publish it.
        Assert.True(lexicon.PlacesByCountry["UA"] > lexicon.PlacesByCountry["MM"]);
        Assert.True(lexicon.PlacesByCountry.Count > 200);

        output.WriteLine(
            $"UA {lexicon.PlacesByCountry["UA"]}, MM {lexicon.PlacesByCountry["MM"]}, "
            + $"across {lexicon.PlacesByCountry.Count} countries, "
            + $"{lexicon.PlacesByCountry.Count(entry => entry.Value < 20)} held by fewer than 20 names.");

        // Ukraine's total is both layers added together, not whichever one answered last.
        Assert.True(lexicon.PlacesByCountry["UA"] > TheatrePlaces.CountsByTheatre["Ukraine"]);
    }
}
