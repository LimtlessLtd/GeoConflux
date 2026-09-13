using Geopolitics.Infrastructure.Location;
using Xunit.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// The theatre depth this sprint exists to add, asserted where it is actually visible: a report
/// naming a town in Ukraine, Yemen or Tigray resolves to that town.
/// <para>
/// Before this, Tigray resolved to nothing at all — `ET` gave the Ethiopian country centroid, roughly
/// 600 km from Mekelle. The enum said `Country`, so the globe would not have drawn it as a fix, which
/// is the enum working exactly as designed and still leaves the report with nowhere useful to go.
/// Correctly labelled as useless is still useless.
/// </para>
/// </summary>
public sealed class TheatreGazetteerTests(ITestOutputHelper output)
{
    /// <summary>
    /// The places the sprint's definition of done names, with the coordinates asserted to a tenth of
    /// a degree — about eleven kilometres, which is tight enough to catch a wrong town and loose
    /// enough not to break when the source revises a centroid.
    /// </summary>
    [Theory]
    [InlineData("Mekelle", 13.50, 39.48)]
    [InlineData("Marib", 15.46, 45.33)]
    [InlineData("Pokrovsk", 48.28, 37.18)]
    [InlineData("Zalambesa", 14.40, 39.33)]
    public void ATownNamedInReportingResolvesToItself(string name, double latitude, double longitude)
    {
        Assert.True(Gazetteer.TryResolve(name, out var entry), $"'{name}' did not resolve.");

        // A tenth of a degree is about eleven kilometres: tight enough to catch the wrong town,
        // loose enough to survive the source revising a centroid. Marib passes on this because the
        // governorate centroid sits three kilometres from the city that shares its name.
        Assert.Equal(latitude, entry.Latitude, 1);
        Assert.Equal(longitude, entry.Longitude, 1);
        Assert.NotEqual(PlacePrecision.Country, entry.Precision);
    }

    /// <summary>
    /// Where a name belongs to a governorate, a district and a town at once, the containing unit wins
    /// and is labelled as the area it is. Marib is the case that matters: it is the most-reported
    /// place in Yemen's war, and reading its three administrative levels as three rival places would
    /// have dropped the name entirely.
    /// </summary>
    [Fact]
    public void ANameSharedByNestedAdministrativeUnitsResolvesToTheContainingOne()
    {
        Assert.True(Gazetteer.TryResolve("Marib", out var marib));

        Assert.Equal(PlacePrecision.Region, marib.Precision);
        Assert.Contains("Marib", marib.CanonicalName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same claim from the other side, because "resolves" and "resolves usefully" are different
    /// things. A country centroid is a resolution too.
    /// </summary>
    [Fact]
    public void MekelleIsNotTheEthiopianCountryCentroid()
    {
        Assert.True(Gazetteer.TryResolve("Mekelle", out var mekelle));
        Assert.True(Gazetteer.TryResolve("Ethiopia", out var ethiopia));

        Assert.Equal(PlacePrecision.Country, ethiopia.Precision);
        Assert.NotEqual(PlacePrecision.Country, mekelle.Precision);

        // Degrees rather than kilometres, because the point is the size of the error, not a precise
        // distance: the two are hundreds of kilometres apart and the old lexicon could only offer
        // the second when asked about the first.
        Assert.True(
            Math.Abs(mekelle.Latitude - ethiopia.Latitude) > 3,
            "Mekelle and the Ethiopian centroid should not be close; if they are, the extract is wrong.");
    }

    /// <summary>
    /// Yemen is a district gazetteer by deliberate choice, because that is the precision its
    /// reporting supports — governorate, district, area, and no further. A district is an area, so it
    /// is marked as one rather than presented as a fix.
    /// </summary>
    [Fact]
    public void YemeniGovernoratesResolveAsAreasRatherThanAsPositions()
    {
        Assert.True(Gazetteer.TryResolve("Hadhramaut", out var entry), "Hadhramaut did not resolve.");
        Assert.Equal(PlacePrecision.Region, entry.Precision);
        Assert.Equal("YE", entry.CountryCode);
    }

    /// <summary>
    /// The curated table wins where both layers name the same place, because somebody chose the
    /// curated entry and argued for it in a comment. These are the entries ADR 025 and its
    /// predecessors settled, and a bulk extract must not quietly overrule them.
    /// </summary>
    [Theory]
    [InlineData("Kyiv", 50.450, 30.523)]
    [InlineData("Odesa", 46.482, 30.723)]
    [InlineData("Sanaa", 15.369, 44.191)]
    [InlineData("Aden", 12.785, 45.019)]
    public void CuratedEntriesSurviveTheExtract(string name, double latitude, double longitude)
    {
        Assert.True(Gazetteer.TryResolve(name, out var entry));

        Assert.Equal(latitude, entry.Latitude, 3);
        Assert.Equal(longitude, entry.Longitude, 3);
    }

    /// <summary>
    /// Curated aliases keep working, including the ones that exist because two communities spell the
    /// same place differently. Bulk data does not make those judgements.
    /// </summary>
    [Theory]
    [InlineData("Kiev", "Kyiv")]
    [InlineData("Odessa", "Odesa")]
    [InlineData("Sana'a", "Sanaa")]
    [InlineData("Arabian Gulf", "Persian Gulf")]
    public void CuratedAliasesSurviveTheExtract(string alias, string expected)
    {
        Assert.True(Gazetteer.TryResolve(alias, out var entry));
        Assert.Equal(expected, entry.CanonicalName);
    }

    /// <summary>
    /// A name that denotes more than one place is dropped rather than resolved to whichever candidate
    /// happened to come back first. An unplaced report is visibly unplaced; a confidently misplaced
    /// one is not, and the reader has no way to tell.
    /// </summary>
    [Fact]
    public void AmbiguousSourcedNamesAreDroppedAndCounted()
    {
        output.WriteLine($"Ambiguous names dropped from the extract: {Gazetteer.AmbiguousSourcedNames}");

        // Real place names collide constantly — Ukraine alone has many villages sharing a name — so a
        // count of zero would mean the ambiguity check is not running rather than that the data is
        // unusually clean.
        Assert.True(
            Gazetteer.AmbiguousSourcedNames > 0,
            "No ambiguous names were dropped, which means the collision rule is not being applied.");
    }

    [Fact]
    public void TheExtractCoversAllThreeTheatres()
    {
        foreach (var theatre in new[] { "Ukraine", "Yemen", "Tigray" })
        {
            Assert.True(
                TheatrePlaces.CountsByTheatre.TryGetValue(theatre, out var count) && count > 0,
                $"The extract holds no places for {theatre}.");

            output.WriteLine($"{theatre}: {count} places");
        }
    }

    /// <summary>
    /// A short sourced name is resolvable but is not hunted for inside running prose.
    /// <para>
    /// The two entry points ask different questions. A caller passing "Sad" to the resolver has
    /// asserted that it is a place name; the scanner finding "sad" inside a sentence has guessed. A
    /// bulk extract supplies far too many short, ordinary-looking names for that guess to be safe —
    /// there are real Ukrainian villages named Sad, Rama, Gora and Aura, and Lutsk carries the alias
    /// Luck. Searching for them cost nine points of location-extraction precision before this rule
    /// existed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Sad")]
    [InlineData("Rama")]
    [InlineData("Luck")]
    [InlineData("Mare")]
    public void AShortOrdinaryLookingNameResolvesButIsNotHuntedForInProse(string name)
    {
        Assert.True(Gazetteer.TryResolve(name, out _), $"'{name}' should still resolve when asked about directly.");

        var prose = $"The report noted the {name.ToLowerInvariant()} of it before naming Mekelle.";
        Assert.Equal("Mekele", Gazetteer.FindFirstMention(prose));
    }

    /// <summary>
    /// The rule has to keep the short names that matter. Cities are named by their short names
    /// constantly, and a length rule alone would have thrown Kyiv and Lviv out with Sad and Gora.
    /// </summary>
    [Theory]
    [InlineData("Lviv")]
    [InlineData("Sumy")]
    [InlineData("Uman")]
    [InlineData("Axum")]
    public void AShortNameOfASubstantialPlaceIsStillFoundInProse(string name)
    {
        Assert.Equal(name, Gazetteer.FindFirstMention($"Reporting overnight described shelling near {name} itself."));
    }

    /// <summary>
    /// Every extracted coordinate is a real coordinate. The extractor already refuses to write a file
    /// that fails this, but the file is what ships, so the assertion belongs here too.
    /// </summary>
    [Fact]
    public void EveryExtractedPlaceHasAPlausibleCoordinate()
    {
        Assert.All(TheatrePlaces.All, place =>
        {
            Assert.InRange(place.Latitude, -90, 90);
            Assert.InRange(place.Longitude, -180, 180);
            Assert.False(string.IsNullOrWhiteSpace(place.Name));
            Assert.False(place is { Latitude: 0, Longitude: 0 });
        });
    }
}
