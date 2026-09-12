using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;

namespace Geopolitics.UnitTests;

/// <summary>
/// Exercises the provider parsers against recorded payloads.
/// <para>
/// These are the tests that stand in for the live services. The parsers are the only part of an
/// adapter that has to understand a third party's format, so pinning them to real response shapes is
/// what makes the integrations credible without a credential or a network. The malformed cases matter
/// as much as the well-formed ones: every one of them is a thing a provider has actually been known
/// to send, and the requirement is that the pipeline degrades rather than falls over.
/// </para>
/// </summary>
public sealed class ProviderParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    [Fact]
    public void RssParserReadsItemsAndStripsPublisherMarkup()
    {
        var entries = SyndicationFeedParser.Parse(Fixture("rss-2.0.xml"));

        Assert.Equal(3, entries.Count);

        var first = entries[0];
        Assert.Equal("example-maritime-0001", first.Identifier);
        Assert.Contains("Bab-el-Mandeb", first.Summary, StringComparison.Ordinal);

        // The description arrived as escaped HTML with a tracking pixel in it. What reaches the
        // pipeline should be the sentence, not the markup.
        Assert.DoesNotContain("<", first.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("px.gif", first.Summary, StringComparison.Ordinal);
        // The fixture's pubDate names the wrong weekday, which .NET's parser rejects outright and
        // which real feeds really do publish. The timestamp itself is unambiguous, so it survives.
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 8, 14, 0, TimeSpan.Zero), first.PublishedAt);
    }

    [Fact]
    public void RssParserLeavesTheDateUnsetWhenTheItemOmitsIt()
    {
        var entries = SyndicationFeedParser.Parse(Fixture("rss-2.0.xml"));

        // Absent rather than invented. The pipeline falls back to arrival time, which is a weaker
        // claim but a true one.
        Assert.Null(entries[1].PublishedAt);
    }

    [Fact]
    public void RssParserReadsNamespacedContentAndDateElements()
    {
        var entries = SyndicationFeedParser.Parse(Fixture("rss-2.0.xml"));
        var third = entries[2];

        Assert.Contains("Strait of Hormuz", third.Summary, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 11, 30, 0, TimeSpan.Zero), third.PublishedAt);

        // No guid on this item, so the link is the next best stable identifier.
        Assert.Equal("https://example.invalid/maritime/3", third.Identifier);
    }

    [Fact]
    public void AtomParserReadsEntriesAndFallsBackToTheAlternateLink()
    {
        var entries = SyndicationFeedParser.Parse(Fixture("atom-1.0.xml"));

        Assert.Equal(2, entries.Count);
        Assert.Equal("urn:example:security:0001", entries[0].Identifier);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 9, 45, 0, TimeSpan.Zero), entries[0].PublishedAt);

        Assert.Equal("https://example.invalid/security/2", entries[1].Identifier);
        Assert.Contains("Black Sea", entries[1].Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("<em>", entries[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedFeedIsReportedAsAFormatProblemRatherThanAnXmlOne()
    {
        var exception = Assert.Throws<FormatException>(() => SyndicationFeedParser.Parse(Fixture("rss-malformed.xml")));

        Assert.Contains("not well-formed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FeedCarryingAnExternalEntityDeclarationIsRejected()
    {
        // A feed is attacker-influenced input, so the parser must refuse a document that asks it to
        // dereference a local file rather than dutifully resolving it.
        Assert.Throws<FormatException>(() => SyndicationFeedParser.Parse(Fixture("rss-with-doctype.xml")));
    }

    [Fact]
    public void EmptyFeedResponseIsAnError()
    {
        // Not zero items. An empty body means the provider failed to answer, and reporting it as a
        // quiet news day would hide an outage.
        Assert.Throws<FormatException>(() => SyndicationFeedParser.Parse("   "));
    }

    [Fact]
    public void FirmsParserSkipsRowsItCannotTrustAndKeepsTheRest()
    {
        var hotspots = FirmsCsvParser.Parse(Fixture("firms-viirs.csv"));

        // Six data rows: one has an impossible latitude, one is truncated, one has an unreadable
        // date. The three that survive are the three that are usable.
        Assert.Equal(3, hotspots.Count);
        Assert.All(hotspots, hotspot => Assert.InRange(hotspot.Latitude, -90, 90));
    }

    [Fact]
    public void FirmsParserNormalisesWordConfidenceOntoTheNumericScale()
    {
        var hotspots = FirmsCsvParser.Parse(Fixture("firms-viirs.csv"));

        Assert.Equal(60, hotspots[0].Confidence);
        Assert.Equal(90, hotspots[1].Confidence);
        Assert.Equal(20, hotspots[2].Confidence);
    }

    [Fact]
    public void FirmsParserCombinesTheSeparateDateAndTimeColumns()
    {
        var hotspots = FirmsCsvParser.Parse(Fixture("firms-viirs.csv"));

        // acq_time is an integer HHMM in UTC: 0512 is 05:12.
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 5, 12, 0, TimeSpan.Zero), hotspots[0].AcquiredAt);
    }

    [Fact]
    public void FirmsDetectionIdentifierIsStableAcrossParses()
    {
        var first = FirmsCsvParser.Parse(Fixture("firms-viirs.csv"))[0];
        var second = FirmsCsvParser.Parse(Fixture("firms-viirs.csv"))[0];

        // FIRMS assigns no identifier, so the pipeline's deduplication depends entirely on this key
        // being the same every time the same detection is seen.
        Assert.Equal(first.Identifier, second.Identifier);
        Assert.NotEqual(first.Identifier, FirmsCsvParser.Parse(Fixture("firms-viirs.csv"))[1].Identifier);
    }

    [Fact]
    public void FirmsParserReadsTheModisColumnLayoutToo()
    {
        var hotspots = FirmsCsvParser.Parse(Fixture("firms-modis.csv"));

        Assert.Equal(2, hotspots.Count);

        // MODIS names the brightness column differently from VIIRS and reports confidence as a
        // percentage. Both layouts have to work without the caller choosing a parser.
        Assert.Equal(345.1, hotspots[0].BrightnessKelvin, 1);
        Assert.Equal(78, hotspots[0].Confidence);
        Assert.Equal(22.6, hotspots[0].RadiativePowerMegawatts!.Value, 1);
    }

    [Fact]
    public void FirmsPlainTextErrorIsReportedAsAFailureNotAsZeroDetections()
    {
        // FIRMS answers a bad key with HTTP 200 and a sentence. Treating that as an empty result set
        // would turn a broken credential into a permanently quiet map.
        var exception = Assert.Throws<FormatException>(() => FirmsCsvParser.Parse(Fixture("firms-error.txt")));

        Assert.Contains("Invalid MAP_KEY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcledParserReadsCodedEventsAndSkipsUnusableRows()
    {
        var events = AcledResponseParser.Parse(Fixture("acled-response.json"));

        // Four rows, one without any identifier. A record that cannot be identified cannot be
        // deduplicated, so it is dropped rather than guessed at.
        Assert.Equal(3, events.Count);
        Assert.Equal("UKR99001", events[0].Identifier);
    }

    [Fact]
    public void AcledParserAcceptsNumbersSentAsStringsOrAsNumbers()
    {
        var events = AcledResponseParser.Parse(Fixture("acled-response.json"));

        Assert.Equal(50.450, events[0].Latitude!.Value, 3);
        Assert.Equal(15.4625, events[1].Latitude!.Value, 4);
        Assert.Equal(7, events[1].Fatalities);
    }

    [Fact]
    public void AcledSeverityFollowsTheStatedFatalityBands()
    {
        var events = AcledResponseParser.Parse(Fixture("acled-response.json"));

        Assert.Equal(Severity.Low, events[0].Severity);
        Assert.Equal(Severity.High, events[1].Severity);
        Assert.Equal(Severity.Critical, events[2].Severity);
    }

    [Fact]
    public void AcledEventTypesMapOntoThisSystemsTaxonomy()
    {
        var events = AcledResponseParser.Parse(Fixture("acled-response.json"));

        Assert.Equal(EventType.Protest, events[0].EventType);
        Assert.Equal(EventType.Conflict, events[1].EventType);
        Assert.Equal(EventType.Terrorism, events[2].EventType);
    }

    [Fact]
    public void AcledDropsHalfACoordinatePairRatherThanPlacingTheEventOnTheEquator()
    {
        var events = AcledResponseParser.Parse(Fixture("acled-response.json"));
        var unusable = events[2];

        Assert.Null(unusable.Latitude);
        Assert.Null(unusable.Longitude);

        // The record still arrives. It simply has to earn its position through the resolver, from
        // the place name, like any other report without coordinates — which for Mekelle means it
        // stays unplaced until the gazetteer covers Tigray.
        Assert.Equal("Mekelle", unusable.LocationName);
    }

    /// <summary>
    /// The parser reports the country as ACLED names it and leaves the mapping to a code alone.
    /// <para>
    /// The retired API carried an <c>iso3</c> field; the current schema carries <c>iso</c>, which is
    /// the numeric code, and a name in <c>country</c>. Neither is the alpha-2 the rest of the system
    /// uses, so the parser stays a schema reader and the adapter does the lookup — which keeps the
    /// name-to-code table in one place instead of two.
    /// </para>
    /// </summary>
    [Fact]
    public void AcledReportsTheCountryAsNamedRatherThanAsACode()
    {
        var events = AcledResponseParser.Parse(Fixture("acled-response.json"));

        Assert.Equal("Ukraine", events[0].CountryName);
        Assert.Equal("Yemen", events[1].CountryName);
        Assert.Equal("Ethiopia", events[2].CountryName);
    }

    [Fact]
    public void AcledErrorResponseSurfacesTheProvidersOwnMessage()
    {
        var exception = Assert.Throws<FormatException>(() => AcledResponseParser.Parse(Fixture("acled-error.json")));

        Assert.Contains("Invalid API key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcledNonJsonResponseIsAFormatProblem() =>
        Assert.Throws<FormatException>(() => AcledResponseParser.Parse("<html>gateway timeout</html>"));
}
