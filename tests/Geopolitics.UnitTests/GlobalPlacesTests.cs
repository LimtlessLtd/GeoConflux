using Geopolitics.Infrastructure.Location;
using Xunit.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// The parse for the global extract, which ADR 033 undertook to defend rather than assume.
/// <para>
/// Choosing one line per place over JSON bought 13 MB and a reviewable diff, and the price was a
/// deserialiser that no longer rejects malformed input for free. A short row, a number that is not
/// one, a coordinate off the globe and a precision letter nobody wrote are all things a hand-rolled
/// reader will happily turn into a place, and a place with a shifted column draws a marker somewhere
/// nothing happened. So each of those shapes is asserted here, against text written for the purpose,
/// rather than through a committed file that by construction contains none of them.
/// </para>
/// </summary>
public sealed class GlobalPlacesTests(ITestOutputHelper output)
{
    private const string Header = "# name\tlat\tlon\tcountry\tprecision\trank\tpopulation\tadmin1\tadmin2\tspellings\n";

    private static (GlobalPlace[] Places, int Unreadable) Parse(string text) =>
        GlobalPlaces.Parse(new StringReader(text));

    [Fact]
    public void AWellFormedRowBecomesAPlace()
    {
        var (places, unreadable) = Parse(
            Header + "Kayin State\t17.5\t97.75\tMM\tR\t1\t1574079\t07\t\tKayin|ကရင်ပြည်နယ်|克倫邦\n");

        var place = Assert.Single(places);

        Assert.Equal(0, unreadable);
        Assert.Equal("Kayin State", place.Name);
        Assert.Equal(17.5, place.Latitude);
        Assert.Equal(97.75, place.Longitude);
        Assert.Equal("MM", place.CountryCode);
        Assert.Equal(PlacePrecision.Region, place.Precision);
        Assert.Equal(1, place.Rank);
        Assert.Equal(1_574_079, place.Population);
        Assert.Equal("07", place.Admin1);
        Assert.Equal(string.Empty, place.Admin2);
        Assert.Equal(["Kayin", "ကရင်ပြည်နယ်", "克倫邦"], place.Aliases);
    }

    [Fact]
    public void CommentsAndBlankLinesAreNotRows()
    {
        var (places, unreadable) = Parse(Header + "\n# another comment\nAden\t12.78\t45.02\tYE\tS\t3\t800000\t01\t\t\n\n");

        Assert.Single(places);
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void APlaceWithNoAlternateSpellingsHasNoneRatherThanAnEmptyOne()
    {
        var (places, _) = Parse(Header + "Somewhere\t1\t2\tZZ\tS\t3\t\t\t\t\n");

        Assert.Empty(places[0].Aliases);

        // A source that records no population is not a source that records zero. Only the tie-break
        // uses it, and a zero would silently lose every tie.
        Assert.Null(places[0].Population);
    }

    /// <summary>
    /// Each of these is a row that a looser reader would accept and turn into a marker. The count is
    /// what matters: the row is rejected and said to have been rejected, not dropped quietly.
    /// </summary>
    [Theory]
    [InlineData("Truncated\t17.5\t97.75\tMM\tR\t1\n", "a row missing its last fields")]
    [InlineData("Extra\t17.5\t97.75\tMM\tR\t1\t0\t\t\t\tstray\n", "a row with a field too many")]
    [InlineData("\t17.5\t97.75\tMM\tR\t1\t0\t\t\t\n", "a row with no name")]
    [InlineData("Nowhere\t17.5\t97.75\t\tR\t1\t0\t\t\t\n", "a row with no country")]
    [InlineData("Bad\tnorth\t97.75\tMM\tR\t1\t0\t\t\t\n", "a latitude that is not a number")]
    [InlineData("Bad\t17.5\teast\tMM\tR\t1\t0\t\t\t\n", "a longitude that is not a number")]
    [InlineData("Bad\t17.5\t97.75\tMM\tR\tfirst\t0\t\t\t\n", "a rank that is not a number")]
    [InlineData("Bad\t91\t97.75\tMM\tR\t1\t0\t\t\t\n", "a latitude off the globe")]
    [InlineData("Bad\t17.5\t181\tMM\tR\t1\t0\t\t\t\n", "a longitude off the globe")]
    [InlineData("Bad\t17.5\t97.75\tMM\tX\t1\t0\t\t\t\n", "a precision letter nobody writes")]
    public void ARowThatCannotBeTrustedIsRejectedAndCounted(string row, string why)
    {
        var (places, unreadable) = Parse(Header + row);

        output.WriteLine($"{why}: {unreadable} rejected");

        Assert.Empty(places);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void AGoodRowSurvivesABadNeighbour()
    {
        // One unreadable line is a defect worth counting and surviving. Refusing to start over it
        // would lose an entire lexicon for one district.
        var (places, unreadable) = Parse(
            Header
            + "Kidal\t18.44\t1.41\tML\tS\t3\t25617\t8\t\t\n"
            + "Broken\tnorth\t1.41\tML\tS\t3\t0\t8\t\t\n"
            + "Gao\t16.27\t-0.04\tML\tS\t3\t86633\t7\t\t\n");

        Assert.Equal(2, places.Length);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void ThePrecisionLetterDecidesHowCloselyTheCoordinateDescribesTheReport()
    {
        var (places, _) = Parse(
            Header
            + "Town\t1\t2\tZZ\tS\t3\t0\t\t\t\n"
            + "District\t1\t2\tZZ\tR\t2\t0\t\t\t\n"
            + "Whole country\t1\t2\tZZ\tC\t1\t0\t\t\t\n");

        Assert.Equal(
            [PlacePrecision.Settlement, PlacePrecision.Region, PlacePrecision.Country],
            places.Select(place => place.Precision));
    }

    [Fact]
    public void TheCommittedExtractParsesCompletely()
    {
        // The number that must stay zero. It silently stopping being zero is how a format drift goes
        // unnoticed until a country quietly empties.
        output.WriteLine(
            $"{GlobalPlaces.All.Count:N0} places in {GlobalPlaces.CountsByCountry.Count} countries, "
            + $"{GlobalPlaces.UnreadableRows} unreadable rows.");

        Assert.Equal(0, GlobalPlaces.UnreadableRows);
        Assert.NotEmpty(GlobalPlaces.All);
    }

    [Fact]
    public void TheCommittedExtractSpansEveryPopulatedContinent()
    {
        // The point of the sprint, asserted as coverage rather than as a total: a lexicon of 78,000
        // places all in one region would satisfy a count and none of the intent.
        string[] everywhere = ["MM", "ML", "CO", "CD", "SO", "UA", "YE", "ET", "PH", "MX", "AU", "BR", "NG", "IN"];

        foreach (var country in everywhere)
        {
            Assert.True(
                GlobalPlaces.CountsByCountry.ContainsKey(country),
                $"The extract holds no places at all for {country}, so nothing reported there can be drawn.");
        }
    }

    [Fact]
    public void EveryPlaceCarriesACoordinateThatCouldExist()
    {
        // Cheap, and it covers the whole committed file rather than the rows a test happened to name.
        // A shifted column anywhere in 78,000 rows shows up here.
        var impossible = GlobalPlaces.All
            .Where(place => place.Latitude is < -90 or > 90 || place.Longitude is < -180 or > 180)
            .ToArray();

        Assert.Empty(impossible);
    }
}
