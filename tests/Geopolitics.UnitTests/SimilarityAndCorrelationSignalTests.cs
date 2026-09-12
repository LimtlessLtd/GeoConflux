using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Options;

namespace Geopolitics.UnitTests;

public sealed class LexicalTextSimilarityTests
{
    private readonly LexicalTextSimilarity similarity = new();

    [Fact]
    public void IdenticalTextScoresOne() =>
        Assert.Equal(1, similarity.Score(
            "A cargo vessel reported an approach near Bab-el-Mandeb.",
            "A cargo vessel reported an approach near Bab-el-Mandeb."));

    [Fact]
    public void TextsSharingNoMeaningfulWordsScoreZero() =>
        Assert.Equal(0, similarity.Score(
            "Ransomware disrupted a logistics operator.",
            "Volcanic ash closed regional airspace."));

    [Fact]
    public void EmptyOrMissingTextScoresZero()
    {
        // Nothing is not similar to anything. Returning a high score for two empty strings would
        // make every unenriched observation look like a match for every other one.
        Assert.Equal(0, similarity.Score(null, "Some text."));
        Assert.Equal(0, similarity.Score("Some text.", "   "));
        Assert.Equal(0, similarity.Score(null, null));
    }

    [Fact]
    public void SharedFunctionWordsDoNotCreateSimilarity()
    {
        // Both sentences share "the", "of", "and", "was". Without stopword removal this pair would
        // score as a partial match purely on grammar.
        var score = similarity.Score(
            "The mayor of the city was re-elected and the council was dissolved.",
            "The price of the shipment was agreed and the contract was signed.");

        Assert.True(score < 0.2, $"Expected function words to be discounted, got {score}.");
    }

    [Fact]
    public void DifferentlyWordedReportsOfOneEventScoreHighly()
    {
        // The case correlation actually depends on: two outlets, different phrasing, same event.
        var score = similarity.Score(
            "A cargo ship transiting the Bab-el-Mandeb strait reported being approached by two small skiffs before naval escorts intervened.",
            "Two skiffs approached a cargo vessel in the Bab-el-Mandeb strait; naval escorts responded and the vessel was not boarded.");

        Assert.True(score >= 0.4, $"Expected two accounts of one event to clear the threshold, got {score}.");
    }

    [Fact]
    public void UnrelatedReportsOfTheSameCategoryScoreBelowTheThreshold()
    {
        // The case correlation must not mistake for a match: same category, different event.
        var score = similarity.Score(
            "A demonstration over fuel prices filled the central square in Bamako.",
            "Dockworkers in Lagos began a walkout over unpaid wages.");

        Assert.True(score < 0.4, $"Expected unrelated protests to stay below the threshold, got {score}.");
    }

    [Fact]
    public void AHyphenatedPlaceNameIsTreatedAsOneToken()
    {
        // Splitting on the hyphen would reduce the strongest signal in the sentence to the tokens
        // "bab", "el", and "mandeb", two of which are below the minimum token length.
        var score = similarity.Score("Bab-el-Mandeb transit", "Bab-el-Mandeb transit");
        Assert.Equal(1, score);

        Assert.True(similarity.Score("Bab-el-Mandeb strait", "Mandeb strait") < 1);
    }

    [Fact]
    public void LengthAsymmetryIsNotTreatedAsDisagreement()
    {
        // Every meaningful word of the snap appears in the article, which then goes on at length
        // about unrelated matters. Jaccard reads that padding as disagreement and collapses the
        // score; dividing by the geometric mean of the two token counts does not.
        var snap = "Skiffs approached a tanker near Bab-el-Mandeb.";
        var article = "Skiffs approached a tanker near Bab-el-Mandeb earlier today, according to an "
            + "invented shipping circular that also discussed insurance premiums, convoy scheduling, "
            + "crew rotation arrangements, port congestion at unrelated terminals, and seasonal "
            + "weather patterns across the wider region during the previous quarter.";

        var score = similarity.Score(snap, article);

        // Jaccard over the same token sets scores this pair at roughly 0.13. The measure in use
        // keeps it meaningful without pretending a subsumed snap is a full match, which the overlap
        // coefficient would do by scoring it 1.0 and merging every short report into every long one.
        Assert.True(score > 0.25, $"Expected padding to be discounted, got {score}.");
        Assert.True(score < 0.6, $"Expected a partial match rather than a full one, got {score}.");
    }

    [Fact]
    public void ScoringIsSymmetric()
    {
        var left = "Naval escorts responded to an approach near the strait.";
        var right = "An approach near the strait drew a response from naval escorts.";

        Assert.Equal(similarity.Score(left, right), similarity.Score(right, left));
    }

    [Fact]
    public void TheMethodSaysWhatItActuallyMeasures() =>
        // Recorded alongside decisions that use it, so nobody reading a correlation rationale later
        // mistakes shared vocabulary for a model's judgement about meaning.
        Assert.Equal("lexical-overlap", similarity.Method);
}

public sealed class CorrelationSignalTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly GeoLocation Chokepoint = new("Bab-el-Mandeb", "DJ", 12.585, 43.334);

    [Fact]
    public async Task MeasuredCoLocationOutscoresAMatchingPlaceName()
    {
        var measured = await CorrelateAsync(
            observationLocation: Chokepoint,
            incidentLocation: Chokepoint);

        // Given shared actors as well as a shared name. Without corroboration a bare name match no
        // longer clears the bar at all, which is a separate property asserted on its own below.
        var named = await CorrelateAsync(
            observationLocation: null,
            incidentLocation: Chokepoint,
            observationLocationName: "Bab-el-Mandeb",
            observationEntities: ["Meridian Shipping"],
            incidentEntityKeys: ["meridian shipping"]);

        Assert.True(measured.IsCorrelated);
        Assert.True(named.IsCorrelated);

        // Both are matches, and they are not equally good ones. Agreeing on a label that covers a
        // whole strait is weaker evidence than measuring zero distance, and the score must say so.
        Assert.True(
            measured.Confidence > named.Confidence,
            $"Measured co-location scored {measured.Confidence}, a place-name match scored {named.Confidence}.");
    }

    [Fact]
    public async Task TimeInsideTheWindowIsNotTreatedAsCorroboration()
    {
        // Two reports of the same category, in the same strait, arriving at the same instant, whose
        // text and actors have nothing in common.
        var assessment = await CorrelateAsync(
            observationLocation: null,
            incidentLocation: Chokepoint,
            observationLocationName: "Bab-el-Mandeb",
            observationSummary: "A monk was arrested over an alleged embezzlement scheme.",
            incidentSummary: "An auction house will offer a dress worn by a public figure.");

        // Arriving together is not evidence of being the same event, and against live feeds it is
        // not even discriminating: a single poll stamps everything with the same time. When this was
        // scored as corroboration it merged exactly this pair of real headlines.
        Assert.False(
            assessment.IsCorrelated,
            $"Unrelated reports sharing only a place and a timestamp scored {assessment.Confidence}.");
    }

    [Fact]
    public async Task AReportAtTheEdgeOfTheRadiusStillCorrelates()
    {
        // Roughly 60 km north of the chokepoint, inside the configured 75 km radius.
        var nearby = new GeoLocation("Approach lane", "DJ", 13.124, 43.334);

        var assessment = await CorrelateAsync(observationLocation: nearby, incidentLocation: Chokepoint);

        // The radius has to mean what it says. If confidence decayed to zero at the boundary, the
        // effective radius would silently be half the configured one.
        Assert.True(assessment.IsCorrelated, $"Expected a match inside the radius, got {assessment.Rationale}");
    }

    [Fact]
    public async Task AReportBeyondTheRadiusNeverCorrelatesHoweverWellTheWordingAgrees()
    {
        var faraway = new GeoLocation("Gulf of Guinea", null, 3.0, 3.0);

        var assessment = await CorrelateAsync(
            observationLocation: faraway,
            incidentLocation: Chokepoint,

            // Deliberately identical text, which would otherwise be the strongest possible signal.
            observationSummary: "Skiffs approached a cargo vessel and naval escorts intervened.",
            incidentSummary: "Skiffs approached a cargo vessel and naval escorts intervened.");

        Assert.False(assessment.IsCorrelated);
    }

    [Fact]
    public async Task SharedActorsRaiseConfidenceInAnAlreadyPositionalMatch()
    {
        var without = await CorrelateAsync(
            observationLocation: Chokepoint,
            incidentLocation: Chokepoint,
            observationEntities: ["Task Force 47"],
            incidentEntityKeys: ["unrelated flotilla"]);

        var with = await CorrelateAsync(
            observationLocation: Chokepoint,
            incidentLocation: Chokepoint,
            observationEntities: ["Task Force 47"],
            incidentEntityKeys: ["task force 47"]);

        Assert.True(
            with.Confidence > without.Confidence,
            $"Shared actors scored {with.Confidence}, unrelated actors scored {without.Confidence}.");
        Assert.Contains("shared actor", with.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASatelliteDetectionNamingNobodyIsNotPenalisedForIt()
    {
        // A thermal detection names no actors. Scoring that as total disagreement would make
        // satellite corroboration of a reported incident impossible, which is most of its value.
        var assessment = await CorrelateAsync(
            observationLocation: Chokepoint,
            incidentLocation: Chokepoint,
            observationEntities: [],
            incidentEntityKeys: ["task force 47", "coastal authority"]);

        Assert.True(assessment.IsCorrelated, assessment.Rationale);
    }

    [Fact]
    public async Task TwoUnlocatedReportsOfOneEventCorrelateOnActorsAndWordingTogether()
    {
        var text = "Skiffs approached a cargo vessel and naval escorts intervened before boarding.";

        var assessment = await CorrelateAsync(
            observationLocation: null,
            incidentLocation: null,
            observationSummary: text,
            incidentSummary: text,
            observationEntities: ["Task Force 47"],
            incidentEntityKeys: ["task force 47"]);

        // This is the third deduplication layer doing its job: the same event, reported twice, with
        // neither report placing itself.
        Assert.True(assessment.IsCorrelated, assessment.Rationale);
        Assert.Contains("no location", assessment.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchingWordingAloneIsNotEnoughWithoutSharedActors()
    {
        var text = "Skiffs approached a cargo vessel and naval escorts intervened before boarding.";

        var assessment = await CorrelateAsync(
            observationLocation: null,
            incidentLocation: null,
            observationSummary: text,
            incidentSummary: text,
            observationEntities: [],
            incidentEntityKeys: []);

        // Both signals are required precisely because either alone is routinely true of unrelated
        // reports that merely share a category and a house style.
        Assert.False(assessment.IsCorrelated);
    }

    [Fact]
    public async Task SharedActorsAloneAreNotEnoughWithoutMatchingWording()
    {
        var assessment = await CorrelateAsync(
            observationLocation: null,
            incidentLocation: null,
            observationSummary: "Dockworkers began a walkout over unpaid wages at the terminal.",
            incidentSummary: "A fuel convoy was rerouted after a bridge closure inland.",
            observationEntities: ["Task Force 47"],
            incidentEntityKeys: ["task force 47"]);

        // One actor can be involved in several distinct events on the same day.
        Assert.False(assessment.IsCorrelated);
    }

    [Fact]
    public async Task TheRationaleNamesWhatEstablishedCoLocationFirst()
    {
        var assessment = await CorrelateAsync(observationLocation: Chokepoint, incidentLocation: Chokepoint);

        // A correlation decision has to be reviewable afterwards, and the thing worth reading first
        // is what actually put the two reports in the same place.
        var detail = assessment.Rationale[(assessment.Rationale.IndexOf(':', StringComparison.Ordinal) + 1)..].TrimStart();
        Assert.StartsWith("0.0 km apart", detail, StringComparison.Ordinal);
    }

    private static async Task<Application.Abstractions.CorrelationAssessment> CorrelateAsync(
        GeoLocation? observationLocation,
        GeoLocation? incidentLocation,
        string? observationLocationName = null,
        string observationSummary = "A cargo vessel reported an approach by small craft.",
        string incidentSummary = "A vessel reported an approach in the strait.",
        string[]? observationEntities = null,
        string[]? incidentEntityKeys = null)
    {
        var repository = new FakeIncidentRepository();
        var options = Options.Create(new PipelineOptions());

        var incident = new GeopoliticalIncident(
            Guid.NewGuid(),
            "Existing incident",
            incidentSummary,
            EventType.MaritimeIncident,
            Severity.Medium,
            Now.AddHours(-2),
            incidentLocation,
            ObservationProvenance.Polled,
            Now.AddHours(-2));

        incident.MergeEntities(
            (incidentEntityKeys ?? []).Select(key => new ExtractedEntity(key, EntityType.Organisation)),
            Now);

        repository.Seed(incident);

        var observation = new RawObservation(
            Guid.NewGuid(),
            ObservationKind.News,
            "test:source",
            observationSummary,
            Guid.NewGuid().ToString(),
            Now,
            ObservationProvenance.Polled);

        observation.ApplyNormalisation(
            "Report",
            observationSummary,
            EventType.MaritimeIncident,
            Severity.Medium,
            Now,
            observationLocationName);

        if (observationEntities is { Length: > 0 })
        {
            observation.ApplyExtractions(
                null,
                null,
                observationEntities.Select(name => new ExtractedEntity(name, EntityType.Organisation)));
        }

        if (observationLocation is not null)
        {
            observation.ResolveLocation(observationLocation);
        }

        var correlator = new DeterministicIncidentCorrelator(repository, new LexicalTextSimilarity(), options);
        return await correlator.CorrelateAsync(observation, CancellationToken.None);
    }
}

public sealed class IncidentEntityAggregationTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MergingAccumulatesTheUnionOfWhatEvidenceHasNamed()
    {
        var incident = Build();

        Assert.Equal(2, incident.MergeEntities(Entities("Task Force 47", "Coastal Authority"), Now));
        Assert.Equal(1, incident.MergeEntities(Entities("Task Force 47", "Port Operator"), Now));

        Assert.Equal(3, incident.EntityKeys.Count);
        Assert.Contains("port operator", incident.EntityKeys);
    }

    [Fact]
    public void MergingIsCaseInsensitiveSoOneActorCountsOnce()
    {
        var incident = Build();
        incident.MergeEntities(Entities("Task Force 47"), Now);

        Assert.Equal(0, incident.MergeEntities(Entities("TASK FORCE 47"), Now));
        Assert.Single(incident.EntityKeys);
    }

    [Fact]
    public void MergingIsBoundedSoOneIncidentCannotGrowWithoutLimit()
    {
        var incident = Build();
        incident.MergeEntities(
            Enumerable.Range(0, GeopoliticalIncident.MaxEntityKeys + 20).Select(index => new ExtractedEntity($"actor {index}", EntityType.Organisation)),
            Now);

        Assert.Equal(GeopoliticalIncident.MaxEntityKeys, incident.EntityKeys.Count);
    }

    [Fact]
    public void AMergeThatAddsNothingDoesNotTouchTheUpdatedTimestamp()
    {
        var incident = Build();
        incident.MergeEntities(Entities("Task Force 47"), Now);
        var before = incident.UpdatedAt;

        incident.MergeEntities(Entities("Task Force 47"), Now.AddHours(5));

        // An incident's "last updated" should mean something changed, not that something was
        // re-examined; otherwise it stops being a useful signal on the dashboard.
        Assert.Equal(before, incident.UpdatedAt);
    }

    private static IEnumerable<ExtractedEntity> Entities(params string[] names) =>
        names.Select(name => new ExtractedEntity(name, EntityType.Organisation));

    private static GeopoliticalIncident Build() => new(
        Guid.NewGuid(),
        "Incident",
        "Summary.",
        EventType.MaritimeIncident,
        Severity.Medium,
        Now,
        null,
        ObservationProvenance.Polled,
        Now);
}

public sealed class CorrelationGateTests
{
    [Fact]
    public async Task OneCategoryIsHeldExclusively()
    {
        using var gate = new CorrelationGate();

        var first = await gate.AcquireAsync(EventType.Conflict, CancellationToken.None);
        var second = gate.AcquireAsync(EventType.Conflict, CancellationToken.None);

        Assert.False(second.IsCompleted);

        first.Dispose();

        // Releasing lets the waiter through, which is the whole contract.
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task DifferentCategoriesDoNotWaitForEachOther()
    {
        using var gate = new CorrelationGate();

        using var conflict = await gate.AcquireAsync(EventType.Conflict, CancellationToken.None);
        var protest = gate.AcquireAsync(EventType.Protest, CancellationToken.None);

        // Two categories can never be candidates for each other, so serialising them would cost
        // throughput for nothing.
        Assert.True(protest.IsCompleted);
        (await protest).Dispose();
    }

    [Fact]
    public async Task ReleasingTwiceDoesNotLetTwoHoldersIn()
    {
        using var gate = new CorrelationGate();

        var handle = await gate.AcquireAsync(EventType.Piracy, CancellationToken.None);
        handle.Dispose();
        handle.Dispose();

        using var next = await gate.AcquireAsync(EventType.Piracy, CancellationToken.None);
        var contender = gate.AcquireAsync(EventType.Piracy, CancellationToken.None);

        // A double release would raise the semaphore count and silently reinstate the very race the
        // gate exists to prevent.
        Assert.False(contender.IsCompleted);
    }

    [Fact]
    public async Task WaitingIsCancellable()
    {
        using var gate = new CorrelationGate();
        using var held = await gate.AcquireAsync(EventType.Sanctions, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var blocked = gate.AcquireAsync(EventType.Sanctions, cancellation.Token);
        await cancellation.CancelAsync();

        // Shutdown must not be held up by a worker queued behind a gate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }
}
