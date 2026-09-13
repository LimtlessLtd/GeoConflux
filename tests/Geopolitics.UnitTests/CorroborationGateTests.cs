using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// The rule that lets this project read Telegram at all.
/// <para>
/// Everything here turns on one distinction: a post is evidence that a post exists, and two
/// independent posts are evidence of an event. The tests are written as the sequences that actually
/// occur — a claim arrives alone, a second channel says the same thing, a wire follows an hour later
/// — rather than as assertions about the gate's internals, because the sequences are what a reader
/// of the dashboard experiences and they are what must not regress.
/// </para>
/// </summary>
public sealed class CorroborationGateTests
{
    private static readonly SourceAttribution Telegram = SourceAttribution.Post("telegram", "front_line_reports");
    private static readonly SourceAttribution Bluesky = SourceAttribution.Post("bluesky", "observer.bsky.social");

    private static PipelineTestHarness Harness()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(49.9935, 36.2304, "Kharkiv");
        return harness;
    }

    [Fact]
    public async Task ASingleClaimIsHeldAndOpensNoIncident()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                attribution: Telegram),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Held, result.Outcome);
        Assert.Null(result.IncidentId);
        Assert.Empty(harness.Incidents.Committed);

        // Held, not discarded. The claim is stored, classified, placed and announced — everything
        // except the assertion that the thing it describes happened.
        var claim = Assert.Single(harness.Observations.Committed);
        Assert.Equal(ObservationStatus.Uncorroborated, claim.Status);
        Assert.NotNull(claim.Location);
        Assert.Single(harness.Notifier.Observations);
        Assert.Empty(harness.Notifier.Created);
    }

    [Fact]
    public async Task PublishedReportingNeedsNothingToAgreeWithIt()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();

        var result = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "rss:example-wire",
                sourceIdentifier: "w-1"),
            CancellationToken.None);

        // The gate applies to claims and to nothing else. A wire item, a coded dataset record and a
        // satellite detection each stand on their own, and a rule that held them would be a quiet
        // outage rather than a safeguard.
        Assert.Equal(ProcessingOutcome.Persisted, result.Outcome);
        Assert.True(result.IncidentCreated);
    }

    [Fact]
    public async Task TwoIndependentChannelsAgreeingOnAPlaceOpenOneIncidentBetweenThem()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        var first = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                occurredAt: occurredAt,
                attribution: Telegram),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Held, first.Outcome);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "collected:bluesky/observer.bsky.social",
                sourceIdentifier: "b-1",
                occurredAt: occurredAt.AddMinutes(5),
                attribution: Bluesky),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Persisted, second.Outcome);
        Assert.True(second.IncidentCreated);

        // One incident holding both claims, not one incident and an orphan. The first claim was
        // released by the same save that created the incident.
        var incident = Assert.Single(harness.Incidents.Committed);
        Assert.Equal(2, incident.ObservationCount);
        Assert.All(harness.Observations.Committed, claim => Assert.Equal(ObservationStatus.Persisted, claim.Status));
        Assert.All(harness.Observations.Committed, claim => Assert.Equal(incident.Id, claim.IncidentId));
    }

    [Fact]
    public async Task OneChannelPostingTwiceIsStillOneSource()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                occurredAt: occurredAt,
                attribution: Telegram),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Further explosions reported in the northern districts.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-2",
                occurredAt: occurredAt.AddMinutes(5),
                attribution: Telegram),
            CancellationToken.None);

        // The rule that stops one account manufacturing an incident by repeating itself, which is
        // the cheapest possible attack on a gate like this.
        Assert.Equal(ProcessingOutcome.Held, second.Outcome);
        Assert.Empty(harness.Incidents.Committed);
        Assert.Equal(2, harness.Observations.Committed.Count);
    }

    [Fact]
    public async Task TwoAnonymousSubmissionsCannotCorroborateEachOther()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "manual:one",
                sourceIdentifier: "m-1",
                occurredAt: occurredAt,
                attribution: SourceAttribution.Unattributed),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "manual:two",
                sourceIdentifier: "m-2",
                occurredAt: occurredAt.AddMinutes(5),
                attribution: SourceAttribution.Unattributed),
            CancellationToken.None);

        // "Two anonymous strangers agreed" is one unverifiable assertion repeated, not two sources.
        // Without this, anyone on the network could manufacture an incident by posting twice.
        Assert.Equal(ProcessingOutcome.Held, second.Outcome);
        Assert.Empty(harness.Incidents.Committed);
    }

    [Fact]
    public async Task ClaimsPlacedFarApartDoNotCorroborate()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveByName(new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            ["Kharkiv"] = (49.9935, 36.2304),
            ["Dnipro"] = (48.4647, 35.0462),
        });

        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported overnight.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                occurredAt: occurredAt,
                locationName: "Kharkiv",
                attribution: Telegram),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported overnight.",
                sourceName: "collected:bluesky/observer.bsky.social",
                sourceIdentifier: "b-1",
                occurredAt: occurredAt.AddMinutes(5),
                locationName: "Dnipro",
                attribution: Bluesky),
            CancellationToken.None);

        // Roughly 200 km apart. Two channels that each know where they are and disagree are
        // describing two events, and identical wording does not make them one.
        Assert.Equal(ProcessingOutcome.Held, second.Outcome);
        Assert.Empty(harness.Incidents.Committed);
    }

    [Fact]
    public async Task ClaimsInDifferentLanguagesCorroborateOnPositionAlone()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Повідомляють про вибухи у північних районах міста.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                eventType: EventType.Conflict,
                occurredAt: occurredAt,
                attribution: Telegram),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts of the city.",
                sourceName: "collected:bluesky/observer.bsky.social",
                sourceIdentifier: "b-1",
                eventType: EventType.Conflict,
                occurredAt: occurredAt.AddMinutes(5),
                attribution: Bluesky),
            CancellationToken.None);

        // The property the whole tier exists for. These two texts share almost no vocabulary, so a
        // gate that demanded agreement in wording would have quietly restricted corroboration to
        // claims written in the same language — and the reason to read open social at all is to
        // hear an event described by people who are not reading each other.
        Assert.Equal(ProcessingOutcome.Persisted, second.Outcome);
        Assert.Equal(2, Assert.Single(harness.Incidents.Committed).ObservationCount);
    }

    [Fact]
    public async Task AHeldClaimIsReleasedWhenAWireCatchesUp()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-40);

        var held = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                eventType: EventType.Conflict,
                occurredAt: occurredAt,
                attribution: Telegram),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Held, held.Outcome);
        harness.Notifier.Observations.Clear();

        var wire = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Explosions reported in the northern districts.",
                sourceName: "rss:example-wire",
                sourceIdentifier: "w-1",
                eventType: EventType.Conflict,
                occurredAt: occurredAt.AddMinutes(30)),
            CancellationToken.None);

        // The ordinary sequence, not the exotic one: social breaks first and the wire follows.
        // Without this sweep the gate would be a way of losing the fastest reporting.
        Assert.Equal(ProcessingOutcome.Persisted, wire.Outcome);

        var incident = Assert.Single(harness.Incidents.Committed);
        Assert.Equal(2, incident.ObservationCount);

        var claim = harness.Observations.Committed.Single(value => value.Id == held.ObservationId);
        Assert.Equal(ObservationStatus.Persisted, claim.Status);
        Assert.Equal(incident.Id, claim.IncidentId);

        // Re-announced, so a reader watching the page sees the label change from an uncorroborated
        // claim to part of an incident without reloading.
        Assert.Contains(harness.Notifier.Observations, published => published.Id == claim.Id);
    }

    [Fact]
    public async Task AgreementOnACountryAloneIsNotCorroboration()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveByName(
            new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["Yemen"] = (15.55, 48.52) },
            LocationPrecision.Country);

        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddHours(-6);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Fighting was reported on Tuesday.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                occurredAt: occurredAt,
                locationName: "Yemen",
                attribution: Telegram),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "Fighting was reported on Tuesday.",
                sourceName: "collected:bluesky/observer.bsky.social",
                sourceIdentifier: "b-1",
                occurredAt: occurredAt.AddHours(2),
                locationName: "Yemen",
                attribution: Bluesky),
            CancellationToken.None);

        // Where the gate parts company with the correlator, which does allow country-level
        // agreement at a reduced confidence. "Both somewhere in Yemen" would let two unrelated
        // posts on a busy day assert an event between them, and not doing that is the gate's job.
        Assert.Equal(ProcessingOutcome.Held, second.Outcome);
        Assert.Empty(harness.Incidents.Committed);
    }

    [Fact]
    public async Task ClaimsOfDifferentCategoriesDoNotCorroborate()
    {
        var harness = Harness();
        var processor = harness.BuildProcessor();
        var occurredAt = harness.Clock.GetUtcNow().AddMinutes(-20);

        await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "An explosion was reported downtown.",
                sourceName: "collected:telegram/front_line_reports",
                sourceIdentifier: "t-1",
                eventType: EventType.Conflict,
                occurredAt: occurredAt,
                attribution: Telegram),
            CancellationToken.None);

        var second = await processor.ProcessAsync(
            PipelineTestHarness.Envelope(
                "A protest was reported downtown.",
                sourceName: "collected:bluesky/observer.bsky.social",
                sourceIdentifier: "b-1",
                eventType: EventType.Protest,
                occurredAt: occurredAt.AddMinutes(5),
                attribution: Bluesky),
            CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Held, second.Outcome);
        Assert.Empty(harness.Incidents.Committed);
    }

    [Fact]
    public void PublishedReportingCannotBeHeld()
    {
        var observation = new RawObservation(
            Guid.CreateVersion7(),
            ObservationKind.News,
            "rss:example-wire",
            "Explosions reported.",
            null,
            DateTimeOffset.UtcNow,
            ObservationProvenance.Polled);

        // A bug that routed a wire item through the gate would look exactly like a quiet outage:
        // the map would keep filling with claims while incidents stopped opening, and nothing would
        // say why. The domain refuses rather than letting that be expressible.
        Assert.Throws<DomainException>(observation.HoldAsUncorroborated);
    }
}
