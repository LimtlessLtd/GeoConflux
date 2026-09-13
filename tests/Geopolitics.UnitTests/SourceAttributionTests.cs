using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Briefs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// Pins the distinction the corroboration gate is built on: whether an organisation is standing
/// behind a record, or an account is.
/// <para>
/// It is worth testing separately from the gate because the gate is only as good as this. A rule
/// that holds back user-generated claims is worth nothing if a Telegram post arrives labelled as a
/// wire story, and the failure would be silent — the map would look right, the counts would look
/// right, and the one thing that had been promised would not be true.
/// </para>
/// </summary>
public sealed class SourceAttributionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"geoconflux-tier-{Guid.NewGuid():N}");

    public SourceAttributionTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<IReadOnlyList<ObservationEnvelope>> ReadValidBundleAsync()
    {
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "osint", "bundle-valid.json"),
            Path.Combine(directory, "bundle-valid.json"));

        var source = new AgentBriefEventSource(
            Options.Create(new AgentBriefOptions { Directory = directory }),
            new FakeTimeProvider(Now),
            NullLogger<AgentBriefEventSource>.Instance);

        return await source.ReadBatchAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ACollectedDocumentIsPublishedReporting()
    {
        var envelopes = await ReadValidBundleAsync();
        var document = envelopes.Single(envelope => envelope.SourceName == "collected:Example News Agency");

        Assert.Equal(SourceTier.Published, document.Attribution.Tier);
        Assert.False(document.Attribution.IsClaim);

        // A publisher is not a channel and must not be stored in one. The name is already the source
        // name; a second copy here is a field that can disagree with itself.
        Assert.Null(document.Attribution.Platform);
        Assert.Null(document.Attribution.Channel);
    }

    [Fact]
    public async Task ACollectedPostIsAClaimThatNamesItsChannel()
    {
        var envelopes = await ReadValidBundleAsync();
        var post = envelopes.Single(envelope => envelope.SourceName == "collected:telegram/example_channel");

        Assert.Equal(SourceTier.UserGenerated, post.Attribution.Tier);
        Assert.True(post.Attribution.IsClaim);
        Assert.Equal("telegram", post.Attribution.Platform);
        Assert.Equal("example_channel", post.Attribution.Channel);
        Assert.Equal("telegram/example_channel", post.Attribution.Identity);
    }

    [Fact]
    public async Task ACollectorsStatedLanguageSurvivesToTheRecord()
    {
        var envelopes = await ReadValidBundleAsync();

        // Previously dropped on the floor, which left the language of every collected item resting
        // on whether an enrichment model happened to be configured. A coverage-by-language figure
        // published on that footing would be reporting the model's availability, not the collection.
        Assert.Equal(
            ["ar", "en", "ru", "zh"],
            envelopes.Select(envelope => envelope.DeclaredLanguage).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnAdapterThatSaysNothingIsTreatedAsPublishedReporting()
    {
        var envelope = new ObservationEnvelope
        {
            SourceName = "rss:bbc-world",
            Kind = ObservationKind.News,
            Content = "Shelling reported overnight.",
        };

        // The safe default runs the opposite way from the one on Provenance, and deliberately.
        // Mislabelling a wire as a claim would quietly gate published reporting behind a rule it was
        // never subject to, and the symptom — an incident that simply never opens — is invisible.
        Assert.Equal(SourceTier.Published, envelope.Attribution.Tier);
    }

    [Theory]
    [InlineData("", "somechannel")]
    [InlineData("telegram", "")]
    [InlineData("  ", "  ")]
    public void APostMustNameBothItsPlatformAndItsChannel(string platform, string channel) =>
        Assert.Throws<DomainException>(() => SourceAttribution.Post(platform, channel));

    [Fact]
    public void AHandleIsOnlyUniqueWithinItsPlatform()
    {
        // Two unrelated accounts that happen to share a name are not one source, and an identity
        // that dropped the platform would let either stand in for the other as corroboration.
        Assert.NotEqual(
            SourceAttribution.Post("telegram", "reuters").Identity,
            SourceAttribution.Post("mastodon", "reuters").Identity);
    }

    [Fact]
    public void APlatformIsComparedInOneCase()
    {
        // Bundles are written by hand as often as by a collector, and "Telegram" and "telegram" are
        // the same platform. Folding at construction keeps every later comparison an ordinal one.
        Assert.Equal("telegram/Ukraine_News", SourceAttribution.Post("  Telegram ", " Ukraine_News ").Identity);
    }

    [Fact]
    public void AStatedLanguageIsNotErasedByAModelThatDetectedNone()
    {
        var observation = Observation(SourceAttribution.Post("telegram", "example"), "uk");

        observation.ApplyExtractions(detectedLanguage: null, locationName: null, []);

        // The case that actually bites. An enrichment model answering with no language would
        // otherwise turn a known Ukrainian item into an item of unknown language, and the coverage
        // count would then report one fewer language read than was read.
        Assert.Equal("uk", observation.DetectedLanguage);
    }

    [Fact]
    public void AModelMayNameTheLanguageOfASourceThatStatedNone()
    {
        var observation = Observation(SourceAttribution.Published, declaredLanguage: null);

        observation.ApplyExtractions("fa", locationName: null, []);

        Assert.Equal("fa", observation.DetectedLanguage);
        Assert.Null(observation.DeclaredLanguage);
    }

    [Fact]
    public void TheAttributionIsRebuiltFromTheStoredColumnsRatherThanKeptBesideThem()
    {
        var observation = Observation(SourceAttribution.Post("bluesky", "example.bsky.social"), "en");

        Assert.Equal(SourceAttribution.Post("bluesky", "example.bsky.social"), observation.Attribution);
    }

    private static RawObservation Observation(SourceAttribution attribution, string? declaredLanguage) => new(
        Guid.CreateVersion7(Now),
        ObservationKind.News,
        "collected:test",
        "Content.",
        sourceIdentifier: null,
        Now,
        ObservationProvenance.Collected,
        collectedAt: Now,
        attribution,
        declaredLanguage);
}
