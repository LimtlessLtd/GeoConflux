using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Briefs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// Exercises the collected-bundle source against files on disk.
/// <para>
/// The properties worth pinning are the ones that make this source usable where the polling adapters
/// are not: it opens nothing, it needs no credential, and it produces the same envelopes every time.
/// The rest is about refusing to let a bundle claim more than a citation.
/// </para>
/// </summary>
public sealed class AgentBriefSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"geoconflux-briefs-{Guid.NewGuid():N}");

    public AgentBriefSourceTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void Copy(string fixture, string? name = null) =>
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "osint", fixture),
            Path.Combine(directory, name ?? fixture));

    private AgentBriefEventSource Source(Action<AgentBriefOptions>? configure = null)
    {
        var options = new AgentBriefOptions { Directory = directory };
        configure?.Invoke(options);

        return new AgentBriefEventSource(
            Options.Create(options),
            new FakeTimeProvider(Now),
            NullLogger<AgentBriefEventSource>.Instance);
    }

    [Fact]
    public async Task AValidBundleBecomesCitedObservations()
    {
        Copy("bundle-valid.json");

        var envelopes = await Source().ReadBatchAsync(CancellationToken.None);

        Assert.Equal(5, envelopes.Count);
        Assert.All(envelopes, envelope => Assert.Equal(ObservationProvenance.Collected, envelope.Provenance));
        Assert.All(envelopes, envelope => Assert.StartsWith("collected:", envelope.SourceName, StringComparison.Ordinal));

        // The URL is the identity, which is what makes re-reading a bundle a no-op through the
        // deduplication that already exists rather than through anything this source remembers.
        Assert.All(envelopes, envelope => Assert.StartsWith("http", envelope.SourceIdentifier!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACollectedObservationCarriesItsCollectionTime()
    {
        Copy("bundle-valid.json");

        var envelope = (await Source().ReadBatchAsync(CancellationToken.None))[0];

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 9, 15, 0, TimeSpan.Zero), envelope.CollectedAt);
    }

    [Fact]
    public async Task NoEnvelopeCarriesACoordinate()
    {
        // The bundle contract has no coordinate field, so this cannot fail without someone having
        // added one. That is exactly why it is asserted here as well as on the record type.
        Copy("bundle-valid.json");

        var envelopes = await Source().ReadBatchAsync(CancellationToken.None);

        Assert.All(envelopes, envelope =>
        {
            Assert.Null(envelope.DeclaredLatitude);
            Assert.Null(envelope.DeclaredLongitude);
            Assert.Null(envelope.DeclaredEventType);
            Assert.Null(envelope.DeclaredSeverity);
        });
    }

    [Fact]
    public async Task ANativeScriptPlaceNameIsPassedThroughForTheGazetteerToResolve()
    {
        Copy("bundle-valid.json");

        var envelopes = await Source().ReadBatchAsync(CancellationToken.None);
        var arabic = envelopes
            .Where(envelope => envelope.Content.Contains("باب المندب", StringComparison.Ordinal))
            .ToArray();

        // One from a wire and one from a Mastodon account. A name in a script the lexicon holds is
        // passed through the same way whichever tier carried it, because the tier decides what may
        // be concluded from a record and never how its text is read.
        Assert.Equal(2, arabic.Length);
        Assert.All(arabic, envelope => Assert.Equal("باب المندب", envelope.DeclaredLocationName));
        Assert.Contains(arabic, envelope => envelope.Attribution.Tier == SourceTier.Published);
        Assert.Contains(arabic, envelope => envelope.Attribution.Platform == "mastodon");
    }

    [Fact]
    public async Task ARejectedBundleCostsItsOwnItemsAndNothingElse()
    {
        Copy("bundle-valid.json");
        Copy("bundle-malformed.json");
        Copy("bundle-unknown-version.json");

        var envelopes = await Source().ReadBatchAsync(CancellationToken.None);

        Assert.Equal(5, envelopes.Count);
    }

    [Fact]
    public async Task AnExpiredBundleIsSkipped()
    {
        // The bundle was collected on 12 September. Read it with a one-hour ceiling and it is stale.
        Copy("bundle-valid.json");

        var envelopes = await Source(options => options.MaxBundleAge = TimeSpan.FromHours(1))
            .ReadBatchAsync(CancellationToken.None);

        Assert.Empty(envelopes);
    }

    [Fact]
    public async Task ABundleInsideItsMaximumAgeIsStillRead()
    {
        // The boundary in the other direction: a policy that quietly expired everything would look
        // exactly like a collector that had stopped working.
        Copy("bundle-valid.json");

        var envelopes = await Source(options => options.MaxBundleAge = TimeSpan.FromDays(1))
            .ReadBatchAsync(CancellationToken.None);

        Assert.Equal(5, envelopes.Count);
    }

    [Fact]
    public async Task ADisabledSourceReadsNothing()
    {
        Copy("bundle-valid.json");

        var envelopes = await Source(options => options.Enabled = false).ReadBatchAsync(CancellationToken.None);

        Assert.Empty(envelopes);
    }

    [Fact]
    public async Task AMissingDirectoryIsNothingCollectedRatherThanAFailure()
    {
        Directory.Delete(directory, recursive: true);

        var envelopes = await Source().ReadBatchAsync(CancellationToken.None);

        Assert.Empty(envelopes);
    }

    [Fact]
    public async Task APostAndADocumentAreDistinguishableBySourceName()
    {
        Copy("bundle-valid.json");

        var envelopes = await Source().ReadBatchAsync(CancellationToken.None);

        Assert.Contains(envelopes, envelope => envelope.SourceName == "collected:Example News Agency");
        Assert.Contains(envelopes, envelope => envelope.SourceName == "collected:telegram/example_channel");
    }

    [Fact]
    public async Task ReadingTwiceProducesTheSameEnvelopes()
    {
        // Determinism is the property that lets a published dashboard be rebuilt from the repository
        // and show what it showed before.
        Copy("bundle-valid.json");

        var source = Source();
        var first = await source.ReadBatchAsync(CancellationToken.None);
        var second = await source.ReadBatchAsync(CancellationToken.None);

        Assert.Equal(first, second);
    }
}
