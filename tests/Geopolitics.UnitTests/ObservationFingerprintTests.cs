using Geopolitics.Domain;

namespace Geopolitics.UnitTests;

public sealed class ObservationFingerprintTests
{
    [Fact]
    public void TheSameSourceIdentifierProducesTheSameFingerprint()
    {
        var first = ObservationFingerprint.Compute("wire-a", "story-1", "Original wording.");

        // A source that revises its own story keeps the same identifier, and the revision is the
        // same report rather than a new one.
        var second = ObservationFingerprint.Compute("wire-a", "story-1", "Slightly revised wording.");

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheSameIdentifierFromADifferentSourceIsADifferentReport()
    {
        var first = ObservationFingerprint.Compute("wire-a", "story-1", "Some content.");
        var second = ObservationFingerprint.Compute("wire-b", "story-1", "Some content.");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WithoutAnIdentifierFormattingDifferencesDoNotDefeatDeduplication()
    {
        var first = ObservationFingerprint.Compute("wire-a", null, "Vessel boarded near the strait.");
        var second = ObservationFingerprint.Compute("wire-a", null, "  vessel   boarded, near the STRAIT!  ");

        Assert.Equal(first, second);
    }

    [Fact]
    public void WithoutAnIdentifierDifferentContentProducesDifferentFingerprints()
    {
        var first = ObservationFingerprint.Compute("wire-a", null, "Vessel boarded near the strait.");
        var second = ObservationFingerprint.Compute("wire-a", null, "Protest reported in the capital.");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SourceNameCasingIsIgnored()
    {
        Assert.Equal(
            ObservationFingerprint.Compute("Wire-A", "story-1", "Content."),
            ObservationFingerprint.Compute("wire-a", "STORY-1", "Content."));
    }

    [Fact]
    public void ContentWithNoAlphanumericCharactersCannotBeFingerprinted()
    {
        // There is nothing to identify the report by, so refusing is safer than hashing whitespace
        // and treating every such payload as the same observation.
        Assert.Throws<DomainException>(() => ObservationFingerprint.Compute("wire-a", null, "!!! ??? ..."));
    }
}
