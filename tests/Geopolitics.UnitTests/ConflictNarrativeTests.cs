using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Application.Conflicts;
using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// The evidence floor under the one artefact here that a reader will take as analysis.
/// <para>
/// A model handed three reports writes a paragraph as fluent and as confident as one handed three
/// hundred, and unlike a category, prose carries nothing beside it that a reader checks. So the
/// decision about whether there is enough to characterise is taken from the evidence, before any
/// model is asked, and the refusal is written by this system rather than left to the model's
/// judgement about its own output.
/// </para>
/// </summary>
public sealed class ConflictNarrativeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static Conflict Tigray()
    {
        var conflict = Conflict.Coded("ucdp:333", "Ethiopia: Tigray");
        conflict.RecordCoded("Mekelle town", "ET", events: 90);
        return conflict;
    }

    private static ConflictEvidence Report(string source, int day) => new(
        $"Report {day}",
        "Something was reported.",
        source,
        "Mekelle",
        Now.AddDays(-day));

    private static ConflictNarrativeService Service(
        IReadOnlyList<ConflictEvidence> evidence,
        IConflictNarrator narrator) =>
        new(
            new StubEvidenceRepository { Evidence = evidence },
            new FixedConflictRegister(Tigray()),
            narrator,
            Options.Create(new EnrichmentOptions()),
            new FakeTimeProvider(Now));

    [Fact]
    public async Task BelowTheFloorItDeclinesAndNoModelIsAsked()
    {
        var narrator = new StubNarrator();

        var narrative = await Service(
            [Report("reuters", 1), Report("afp", 2)],
            narrator).BuildAsync("ucdp:333", AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.NotNull(narrative);
        Assert.False(narrative.IsModelWritten);
        Assert.Contains("too little to characterise", narrative.Text, StringComparison.Ordinal);
        Assert.Equal(2, narrative.Reports);

        // Not asked at all. The floor is a property of the evidence, so spending a model call to be
        // told what the count already says would be paying for an opinion about arithmetic.
        Assert.Equal(0, narrator.Calls);
    }

    [Fact]
    public async Task ReportsFromOneSourceAreOneAccountRatherThanApicture()
    {
        var narrator = new StubNarrator();

        var narrative = await Service(
            [.. Enumerable.Range(1, 9).Select(day => Report("reuters", day))],
            narrator).BuildAsync("ucdp:333", AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.NotNull(narrative);
        Assert.False(narrative.IsModelWritten);
        Assert.Contains("all from one source", narrative.Text, StringComparison.Ordinal);
        Assert.Equal(0, narrator.Calls);
    }

    [Fact]
    public async Task AboveTheFloorTheSummaryIsWrittenAndLabelledWithWhatItRestsOn()
    {
        var narrator = new StubNarrator { Text = "Shelling was reported around Mekelle on three days." };

        var narrative = await Service(
            [Report("reuters", 1), Report("afp", 2), Report("reuters", 3), Report("bbc", 4), Report("afp", 5)],
            narrator).BuildAsync("ucdp:333", AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.NotNull(narrative);
        Assert.True(narrative.IsModelWritten);
        Assert.Equal("Shelling was reported around Mekelle on three days.", narrative.Text);
        Assert.Equal(5, narrative.Reports);
        Assert.Equal(3, narrative.Sources);
        Assert.Equal(0.8, narrative.Confidence);
        Assert.Equal(ConflictNarrativePrompt.Version, narrative.PromptVersion);
    }

    [Fact]
    public async Task AsummaryTheModelIsUnsureOfIsWithheldRatherThanQualified()
    {
        var narrator = new StubNarrator { Text = "Something happened somewhere.", Confidence = 0.1 };

        var narrative = await Service(
            [Report("reuters", 1), Report("afp", 2), Report("reuters", 3), Report("bbc", 4), Report("afp", 5)],
            narrator).BuildAsync("ucdp:333", AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.NotNull(narrative);
        Assert.False(narrative.IsModelWritten);
        Assert.DoesNotContain("Something happened", narrative.Text, StringComparison.Ordinal);

        // A confidence figure beside a paragraph does not work: a reader takes in the prose and not
        // the decimal, which is the whole reason prose is the dangerous artefact here.
        Assert.Contains("written and withheld", narrative.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AconflictTheRegisterDoesNotHoldHasNoNarrativeAtAll()
    {
        var narrative = await Service([], new StubNarrator())
            .BuildAsync("ucdp:999999", AnalyticsWindow.Last7Days, CancellationToken.None);

        Assert.Null(narrative);
    }

    [Fact]
    public void TheSchemaHasNoFieldInWhichAmodelCouldAssertAfrontLineOrAcasualtyTotal()
    {
        var properties = ConflictNarrativeContract.ResponseSchema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The shape is the control. A summary and a confidence, and nowhere to put a casualty figure,
        // a unit position, or a projection — the claims this system refuses everywhere else and which
        // a wider contract would quietly re-admit through the one place a model writes prose.
        Assert.Equal(["confidence", "schemaVersion", "summary"], properties);
    }

    private sealed class StubNarrator : IConflictNarrator
    {
        public int Calls { get; private set; }

        public string Text { get; init; } = "A summary.";

        public double Confidence { get; init; } = 0.8;

        public Task<ConflictNarrativeResult> WriteAsync(
            ConflictNarrativeRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new ConflictNarrativeResult(
                new ConflictNarrativeText(Text, Confidence),
                AiInferenceOutcome.Succeeded,
                "stub",
                "stub-model",
                ConflictNarrativePrompt.Version,
                ConflictNarrativeContract.SchemaVersion,
                Attempts: 1,
                LatencyMilliseconds: 1,
                Error: null));
        }
    }

    private sealed class StubEvidenceRepository : IConflictActivityRepository
    {
        public IReadOnlyList<ConflictEvidence> Evidence { get; init; } = [];

        public Task<ConflictActivitySample> SampleAsync(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ConflictActivitySample([], false));

        public Task<IReadOnlyList<ConflictEvidence>> EvidenceAsync(
            string conflictKey,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConflictEvidence>>([.. Evidence.Take(take)]);
    }
}
