using Geopolitics.Infrastructure.Sources.Briefs;

namespace Geopolitics.UnitTests;

/// <summary>
/// Exercises the collection bundle parser against recorded bundles.
/// <para>
/// A bundle is produced by an agent working on this project, and none of these tests give it credit
/// for that. Every fault below is one a collector could commit by being wrong, being lazy, or being
/// successfully redirected by a page it was reading, and the requirement is the same in all three
/// cases: the bundle does not get to decide what the pipeline believes.
/// </para>
/// </summary>
public sealed class CollectionBundleParserTests
{
    /// <summary>After the newest fixture's collection time, so nothing is spuriously "in the future".</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "osint", name));

    private static CollectionBundleReadResult Parse(string name, CollectionBundleLimits? limits = null) =>
        CollectionBundleParser.Parse(Fixture(name), Now, limits);

    [Fact]
    public void AWellFormedBundleYieldsEveryItem()
    {
        var result = Parse("bundle-valid.json");

        Assert.Empty(result.RejectedItems);
        Assert.Equal(5, result.Bundle.Items.Count);
        Assert.Equal("2026-09-12T0915Z-maritime-chokepoints", result.Bundle.BundleId);
        Assert.Equal("maritime-chokepoints", result.Bundle.BriefId);
        Assert.Equal(3, result.Bundle.BriefRevision);
    }

    [Fact]
    public void ADocumentCarriesItsPublisherAndAPostCarriesItsChannel()
    {
        var items = Parse("bundle-valid.json").Bundle.Items;

        var document = items.First(item => item.Kind == CollectedItemKind.Document);
        Assert.Equal("Example News Agency", document.Publisher);
        Assert.Null(document.Platform);
        Assert.Equal("Example News Agency", document.Attribution);

        var post = items.First(item => item.Kind == CollectedItemKind.UserGenerated);
        Assert.Equal("telegram", post.Platform);
        Assert.Equal("example_channel", post.Channel);
        Assert.Null(post.Publisher);
        Assert.Equal("telegram/example_channel", post.Attribution);
    }

    [Fact]
    public void NonLatinTextAndPlaceNamesSurviveIntact()
    {
        var items = Parse("bundle-valid.json").Bundle.Items;

        // Arabic arrives through both tiers, which is the case worth pinning: the same script reaches
        // this parser from a wire and from a Mastodon account, and nothing about the handling differs.
        var arabic = items.Where(item => item.Language == "ar").ToArray();
        Assert.Equal(2, arabic.Length);
        Assert.All(arabic, item => Assert.Contains("باب المندب", item.Excerpt, StringComparison.Ordinal));
        Assert.All(arabic, item => Assert.Equal(["باب المندب"], item.PlaceNames));

        Assert.Equal(
            [CollectedItemKind.Document, CollectedItemKind.UserGenerated],
            arabic.Select(item => item.Kind).Order());

        var chinese = items.Single(item => item.Language == "zh");
        Assert.Equal(["台湾海峡"], chinese.PlaceNames);
    }

    [Fact]
    public void APostIsAcceptedUnderPostedAtRatherThanPublishedAt()
    {
        // A wire publishes and an account posts. Both are the same fact and a bundle may say either.
        var post = Parse("bundle-valid.json").Bundle.Items
            .Single(item => item.Channel == "example_channel");

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 19, 5, 0, TimeSpan.Zero), post.PublishedAt);
    }

    [Theory]
    [InlineData("bundle-malformed.json")]
    [InlineData("bundle-unknown-version.json")]
    [InlineData("bundle-unmapped-property.json")]
    public void AStructuralFaultRejectsTheWholeBundle(string fixture) =>
        Assert.Throws<FormatException>(() => Parse(fixture));

    [Fact]
    public void AnUnmappedPropertyIsRefusedRatherThanIgnored()
    {
        // The fixture carries declaredLatitude. Reading the bundle best-effort would silently drop a
        // property whose whole purpose was to smuggle in a coordinate.
        var exception = Assert.Throws<FormatException>(() => Parse("bundle-unmapped-property.json"));

        Assert.Contains("declaredLatitude", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyRunIsAValidBundle()
    {
        // Nothing found is a real outcome and a different statement from a broken collector.
        var result = Parse("bundle-no-items.json");

        Assert.Empty(result.Bundle.Items);
        Assert.Empty(result.RejectedItems);
    }

    [Fact]
    public void ABundleCollectedInTheFutureIsRefused()
    {
        var exception = Assert.Throws<FormatException>(() =>
            CollectionBundleParser.Parse(
                Fixture("bundle-valid.json"),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Contains("future", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABundleOverItsItemCapIsRefusedRatherThanTruncated()
    {
        // Truncating would publish a partial run while reporting a whole one.
        var exception = Assert.Throws<FormatException>(() =>
            Parse("bundle-valid.json", new CollectionBundleLimits(MaxItems: 2)));

        Assert.Contains("above the limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFaultyItemIsSkippedWhileTheRestOfTheBundleSurvives()
    {
        var result = Parse("bundle-item-faults.json");

        // Exactly one item in that fixture is sound.
        var survivor = Assert.Single(result.Bundle.Items);
        Assert.Equal("https://example-news.org/good", survivor.Url.AbsoluteUri);
        Assert.Equal(11, result.RejectedItems.Count);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("address range")]
    [InlineData("longer than")]
    [InlineData("names no publisher")]
    [InlineData("not an allowed platform")]
    [InlineData("collector-supplied translation")]
    [InlineData("not a sha256")]
    [InlineData("retrieved before it was published")]
    [InlineData("appears more than once")]
    [InlineData("not a recognised item kind")]
    public void EachItemFaultIsRejectedWithAStatedReason(string reason)
    {
        var rejected = Parse("bundle-item-faults.json").RejectedItems;

        Assert.Contains(rejected, entry => entry.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACitedUrlPointingIntoPrivateSpaceIsRefused()
    {
        // Loopback and the cloud metadata address are both in the fixture. A cited URL is followed by
        // the verification tool, so a bundle must not be able to aim it inward.
        var rejected = Parse("bundle-item-faults.json").RejectedItems;

        Assert.Equal(2, rejected.Count(entry => entry.Contains("address range", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ExcerptLengthIsCountedAsAReaderWouldCountIt()
    {
        // Arabic and Han text cost more than one UTF-16 unit per character in places, and a cap
        // measured in units would cut them at a fraction of the stated limit. Every excerpt in the
        // valid fixture is well inside 1000 characters and none may be refused for length.
        var result = Parse("bundle-valid.json", new CollectionBundleLimits(MaxExcerptLength: 200));

        Assert.Empty(result.RejectedItems);
        Assert.Equal(5, result.Bundle.Items.Count);
    }

    [Fact]
    public void ControlCharactersAndBidiOverridesAreStrippedFromQuotedText()
    {
        const string Hostile =
            "{\"schemaVersion\":1,\"bundleId\":\"hostile\",\"collectedAt\":\"2026-09-12T09:00:00Z\"," +
            "\"brief\":{\"id\":\"b\",\"revision\":1},\"items\":[{\"kind\":\"document\"," +
            "\"url\":\"https://example.org/a\",\"publisher\":\"Example\"," +
            "\"publishedAt\":\"2026-09-11T00:00:00Z\",\"retrievedAt\":\"2026-09-12T00:00:00Z\"," +
            "\"contentHash\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\"," +
            "\"excerpt\":\"Reported\\u0007 near\\u202E Aden\\u0000.\",\"placeNames\":[]}]}";

        var item = Assert.Single(CollectionBundleParser.Parse(Hostile, Now).Bundle.Items);

        Assert.DoesNotContain((char)0x07, item.Excerpt);
        Assert.DoesNotContain((char)0x00, item.Excerpt);
        Assert.DoesNotContain((char)0x202E, item.Excerpt);
        Assert.Contains("Aden", item.Excerpt);
    }

    [Fact]
    public void ThePersianZeroWidthNonJoinerIsKept()
    {
        // Unlike a bidi override, the ZWNJ is letter-affecting: stripping it from می‌روم produces a
        // different and wrong spelling. Sanitising has to tell the two apart.
        const string Persian =
            "{\"schemaVersion\":1,\"bundleId\":\"fa\",\"collectedAt\":\"2026-09-12T09:00:00Z\"," +
            "\"brief\":{\"id\":\"b\",\"revision\":1},\"items\":[{\"kind\":\"document\"," +
            "\"url\":\"https://example.org/fa\",\"publisher\":\"Example\"," +
            "\"publishedAt\":\"2026-09-11T00:00:00Z\",\"retrievedAt\":\"2026-09-12T00:00:00Z\"," +
            "\"contentHash\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\"," +
            "\"excerpt\":\"می\\u200Cروم به تهران\",\"placeNames\":[\"تهران\"]}]}";

        var item = Assert.Single(CollectionBundleParser.Parse(Persian, Now).Bundle.Items);

        Assert.Contains((char)0x200C, item.Excerpt);
    }

    [Fact]
    public void TheContractHasNoWayToExpressACoordinate()
    {
        // The strongest guarantee here is not a validation rule, it is the absence of a field. This
        // asserts the absence, so adding one later fails a test that says why it must not exist.
        var properties = typeof(CollectedItem).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(properties, name => name.Contains("Latitude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Longitude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Severity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("EventType", StringComparison.OrdinalIgnoreCase));
    }
}
