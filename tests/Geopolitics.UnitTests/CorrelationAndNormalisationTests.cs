using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Options;

namespace Geopolitics.UnitTests;

public sealed class ObservationNormaliserTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ObservationNormaliser normaliser = new(new KeywordEventClassifier());

    [Fact]
    public void ASourceDeclaredCategoryOverridesTheClassifier()
    {
        var envelope = PipelineTestHarness.Envelope(
            "A ransomware intrusion disrupted operations.",
            eventType: EventType.Sanctions);

        var observation = normaliser.Normalise(envelope, ReceivedAt);

        // The text reads as a cyber incident, but a structured provider's own field wins.
        Assert.Equal(EventType.Sanctions, observation.EventType);
    }

    [Fact]
    public void TheClassifierFillsAGenuineGap()
    {
        var envelope = PipelineTestHarness.Envelope("A ransomware intrusion disrupted port operations.");

        var observation = normaliser.Normalise(envelope, ReceivedAt);

        Assert.Equal(EventType.CyberIncident, observation.EventType);
    }

    [Fact]
    public void AnEventTimeInTheFutureIsClampedToArrival()
    {
        var envelope = PipelineTestHarness.Envelope(
            "A report with a bad clock.",
            occurredAt: ReceivedAt.AddDays(30));

        var observation = normaliser.Normalise(envelope, ReceivedAt);

        // A provider clock error must not let a record dominate recency-weighted views.
        Assert.Equal(ReceivedAt, observation.OccurredAt);
    }

    [Fact]
    public void ATitleIsDerivedFromContentWhenTheSourceSuppliesNone()
    {
        var envelope = PipelineTestHarness.Envelope("Clashes reported near the border. Further detail follows.");

        var observation = normaliser.Normalise(envelope, ReceivedAt);

        Assert.Equal("Clashes reported near the border", observation.Title);
    }

    [Fact]
    public void NormalisationAdvancesTheObservationLifecycle()
    {
        var observation = normaliser.Normalise(PipelineTestHarness.Envelope("Some content."), ReceivedAt);

        Assert.Equal(ObservationStatus.Normalised, observation.Status);
        Assert.NotEmpty(observation.Fingerprint);
    }
}

public sealed class DeterministicIncidentCorrelatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NearbyReportsOfTheSameCategoryCorrelate()
    {
        var (correlator, repository) = Build();
        repository.Seed(Incident(EventType.Piracy, Now.AddHours(-1), new GeoLocation("Bab-el-Mandeb", "DJ", 12.585, 43.334)));

        // ~11 km away and an hour later: the same event as reported by a second outlet.
        var assessment = await correlator.CorrelateAsync(
            Observation(EventType.Piracy, Now, new GeoLocation("Nearby", null, 12.68, 43.36)),
            CancellationToken.None);

        Assert.True(assessment.IsCorrelated);
        Assert.True(assessment.Confidence > 0.5, $"Expected a confident match, got {assessment.Confidence}.");
    }

    [Fact]
    public async Task ReportsBeyondTheRadiusDoNotCorrelate()
    {
        var (correlator, repository) = Build();
        repository.Seed(Incident(EventType.Piracy, Now.AddHours(-1), new GeoLocation("Bab-el-Mandeb", "DJ", 12.585, 43.334)));

        var assessment = await correlator.CorrelateAsync(
            Observation(EventType.Piracy, Now, new GeoLocation("Gulf of Guinea", null, 3.0, 3.0)),
            CancellationToken.None);

        Assert.False(assessment.IsCorrelated);
    }

    [Fact]
    public async Task ReportsOutsideTheTimeWindowDoNotCorrelate()
    {
        var (correlator, repository) = Build();
        repository.Seed(Incident(EventType.Piracy, Now.AddDays(-5), new GeoLocation("Bab-el-Mandeb", "DJ", 12.585, 43.334)));

        var assessment = await correlator.CorrelateAsync(
            Observation(EventType.Piracy, Now, new GeoLocation("Bab-el-Mandeb", "DJ", 12.585, 43.334)),
            CancellationToken.None);

        Assert.False(assessment.IsCorrelated);
    }

    [Fact]
    public async Task DifferentCategoriesNeverCorrelate()
    {
        var (correlator, repository) = Build();
        repository.Seed(Incident(EventType.Protest, Now, new GeoLocation("Beirut", "LB", 33.888, 35.495)));

        var assessment = await correlator.CorrelateAsync(
            Observation(EventType.CyberIncident, Now, new GeoLocation("Beirut", "LB", 33.888, 35.495)),
            CancellationToken.None);

        Assert.False(assessment.IsCorrelated);
    }

    [Fact]
    public async Task WithoutCoordinatesAMatchingPlaceNameCorrelatesButScoresLower()
    {
        var (correlator, repository) = Build();
        repository.Seed(Incident(
            EventType.Protest,
            Now.AddHours(-2),
            new GeoLocation("Beirut", "LB", 33.888, 35.495),
            summary: "Demonstrators blocked the road outside the ministry in Beirut."));

        var observation = Observation(
            EventType.Protest,
            Now,
            location: null,
            locationName: "beirut",
            summary: "Demonstrators blocked the road outside the ministry.");

        var assessment = await correlator.CorrelateAsync(observation, CancellationToken.None);

        Assert.True(assessment.IsCorrelated);

        // A name match without corroborating coordinates is genuinely weaker evidence.
        Assert.True(assessment.Confidence < 0.5, $"Expected a low-confidence match, got {assessment.Confidence}.");
    }

    [Fact]
    public async Task AMatchingPlaceNameAloneIsNotEnoughToMerge()
    {
        var (correlator, repository) = Build();
        repository.Seed(Incident(EventType.Protest, Now.AddHours(-2), new GeoLocation("Beirut", "LB", 33.888, 35.495)));

        // Same category, same place name, same window — and nothing else. No shared wording, no
        // shared actors.
        var observation = Observation(EventType.Protest, Now, location: null, locationName: "beirut");
        var assessment = await correlator.CorrelateAsync(observation, CancellationToken.None);

        // Two protests in one city on one day are routinely two different protests. This used to
        // merge, carried by a temporal signal that was really just "both are inside the window" —
        // which is true of every candidate by construction and therefore evidence of nothing.
        Assert.False(
            assessment.IsCorrelated,
            $"A bare place-name match should not merge, but scored {assessment.Confidence}.");
    }

    [Fact]
    public async Task AnEmptyDatabaseAlwaysOpensANewIncident()
    {
        var (correlator, _) = Build();

        var assessment = await correlator.CorrelateAsync(
            Observation(EventType.Conflict, Now, new GeoLocation("Kyiv", "UA", 50.45, 30.523)),
            CancellationToken.None);

        Assert.False(assessment.IsCorrelated);
        Assert.NotEmpty(assessment.Rationale);
    }

    private static (DeterministicIncidentCorrelator Correlator, FakeIncidentRepository Repository) Build()
    {
        var repository = new FakeIncidentRepository();
        var options = Options.Create(new PipelineOptions());
        return (new DeterministicIncidentCorrelator(repository, new LexicalTextSimilarity(), options), repository);
    }

    private static GeopoliticalIncident Incident(
        EventType eventType,
        DateTimeOffset occurredAt,
        GeoLocation? location,
        string summary = "Summary.") =>
        new(Guid.NewGuid(), "Existing incident", summary, eventType, Severity.Medium, occurredAt, location, false, occurredAt);

    private static RawObservation Observation(
        EventType eventType,
        DateTimeOffset occurredAt,
        GeoLocation? location,
        string? locationName = null,
        string summary = "Summary.")
    {
        var observation = new RawObservation(
            Guid.NewGuid(),
            ObservationKind.News,
            "test:source",
            "Observation content.",
            Guid.NewGuid().ToString(),
            occurredAt,
            false);

        observation.ApplyNormalisation("Title", summary, eventType, Severity.Medium, occurredAt, locationName);

        if (location is not null)
        {
            observation.ResolveLocation(location);
        }

        return observation;
    }
}

public sealed class KeywordEventClassifierTests
{
    private readonly KeywordEventClassifier classifier = new();

    [Theory]
    [InlineData("Skiffs attempted to hijack a cargo vessel.", EventType.Piracy)]
    [InlineData("A ransomware attack disrupted the operator.", EventType.CyberIncident)]
    [InlineData("New export control sanctions were announced.", EventType.Sanctions)]
    [InlineData("A large demonstration filled the square.", EventType.Protest)]
    [InlineData("An earthquake struck the region.", EventType.NaturalHazard)]
    [InlineData("Artillery shelling continued overnight.", EventType.Conflict)]
    public void RecognisableLanguageIsClassified(string text, EventType expected) =>
        Assert.Equal(expected, classifier.Classify(text).EventType);

    [Fact]
    public void UnrecognisedTextFallsBackToOtherWithLowConfidence()
    {
        var classification = classifier.Classify("The committee published its quarterly agenda.");

        Assert.Equal(EventType.Other, classification.EventType);
        Assert.True(classification.Confidence <= 0.2);
    }

    [Fact]
    public void ConfidenceIsNeverPresentedAsCertainty()
    {
        // Keyword matching is a heuristic; reporting a high score would misrepresent it.
        var classification = classifier.Classify("Piracy hijack skiff boarded vessel tanker strait");

        Assert.True(classification.Confidence <= 0.65, $"Confidence {classification.Confidence} is too assertive.");
        Assert.Equal("keyword", classification.Method);
    }

    [Fact]
    public void CasualtyLanguageRaisesSeverity()
    {
        Assert.Equal(Severity.High, classifier.Classify("Several people were killed in the strike.").Severity);
        Assert.Equal(Severity.Critical, classifier.Classify("A chemical weapon was reportedly used.").Severity);
    }

    [Fact]
    public void EmptyTextIsUnknownRatherThanGuessed()
    {
        var classification = classifier.Classify("   ");

        Assert.Equal(EventType.Other, classification.EventType);
        Assert.Equal(Severity.Unknown, classification.Severity);
    }
}
