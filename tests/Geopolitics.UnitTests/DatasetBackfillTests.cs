using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// Walking backwards through a dataset's history: bounded, resumable, and idempotent.
/// <para>
/// The hard part is not fetching old records, it is stopping and starting. A poll gets a fixed
/// number of requests and then has to put the walk down mid-stride in a state the next poll — which
/// may be in a different process, after a deploy, a week later — can pick up without either
/// repeating the whole archive or silently skipping the part it did not finish.
/// </para>
/// </summary>
public sealed class DatasetBackfillTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Where the live window opened, which is where a first-ever walk starts from.</summary>
    private static readonly DateTimeOffset LiveWindowStart = Now.AddDays(-2);

    [Fact]
    public async Task NothingIsBackfilledUntilADeploymentSaysHowFarBackItWants()
    {
        // No BackfillSince. A clone that acquires a credential reads the present and stops there,
        // rather than pulling a decade of coded conflict out of someone else's API unprompted.
        var settings = Settings(backfillSince: null);
        var provider = new RecordingProvider();
        var checkpoints = new FakeCheckpointStore();

        var envelopes = await ReadAsync(settings, provider, new RequestBudget(10), checkpoints);

        Assert.Empty(envelopes);
        Assert.Empty(provider.Requests);
        Assert.Empty(checkpoints.Written);
    }

    [Fact]
    public async Task TheWalkMovesBackwardsOneWindowAtATimeAndRecordsWhereItReached()
    {
        var settings = Settings(backfillSince: Now.AddDays(-30), window: TimeSpan.FromDays(7));
        var provider = new RecordingProvider();
        var checkpoints = new FakeCheckpointStore();

        await ReadAsync(settings, provider, new RequestBudget(3), checkpoints);

        Assert.Equal(3, provider.Requests.Count);

        // Each window ends where the previous one began. A walk that left gaps between its steps
        // would be worse than no walk, because the checkpoint would claim the gaps had been read.
        Assert.Equal(LiveWindowStart, provider.Requests[0].End);
        Assert.Equal(provider.Requests[0].Start, provider.Requests[1].End);
        Assert.Equal(provider.Requests[1].Start, provider.Requests[2].End);
        Assert.All(provider.Requests, window => Assert.Equal(TimeSpan.FromDays(7), window.Length));

        Assert.Equal(LiveWindowStart.AddDays(-21), checkpoints.Written[^1]);
    }

    /// <summary>
    /// The whole point of persisting anything. Without this, every poll would start again at the
    /// present and the walk would never reach further back than a single poll's budget.
    /// </summary>
    [Fact]
    public async Task AWalkResumesFromItsCheckpointRatherThanFromThePresent()
    {
        var settings = Settings(backfillSince: Now.AddDays(-365), window: TimeSpan.FromDays(7));
        var reached = Now.AddDays(-100);
        var provider = new RecordingProvider();
        var checkpoints = new FakeCheckpointStore(reached);

        await ReadAsync(settings, provider, new RequestBudget(1), checkpoints);

        // Carries on from where the last poll stopped, not from where the live window opens.
        Assert.Equal(reached, provider.Requests.Single().End);
        Assert.Equal(reached.AddDays(-7), provider.Requests.Single().Start);
    }

    /// <summary>
    /// The failure this guards against is silent and permanent: advance the checkpoint past a window
    /// that was only half read, and nothing ever goes back for the rest. A map cannot show the events
    /// it was never offered, so the gap would not look like a gap.
    /// </summary>
    [Fact]
    public async Task AWindowLeftUnfinishedDoesNotAdvanceTheCheckpoint()
    {
        var settings = Settings(backfillSince: Now.AddDays(-30), window: TimeSpan.FromDays(7));

        // Truncates everything, so narrowing starts and the budget runs out mid-window.
        var provider = new RecordingProvider(truncated: true);
        var checkpoints = new FakeCheckpointStore();

        await ReadAsync(settings, provider, new RequestBudget(2), checkpoints);

        Assert.NotEmpty(provider.Requests);
        Assert.Empty(checkpoints.Written);
    }

    [Fact]
    public async Task TheWalkStopsAtTheEarliestConfiguredDateRatherThanOvershootingIt()
    {
        var earliest = LiveWindowStart.AddDays(-10);
        var settings = Settings(backfillSince: earliest, window: TimeSpan.FromDays(7));
        var provider = new RecordingProvider();
        var checkpoints = new FakeCheckpointStore();

        await ReadAsync(settings, provider, new RequestBudget(10), checkpoints);

        // Seven days, then the remaining three clipped to the floor: two requests, not two full
        // windows. A deployment that asked for ten days of history gets ten days of requests.
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(earliest, provider.Requests[^1].Start);
        Assert.Equal(earliest, checkpoints.Written[^1]);
    }

    [Fact]
    public async Task AWalkThatHasReachedItsFloorAsksForNothingFurther()
    {
        var earliest = Now.AddDays(-30);
        var settings = Settings(backfillSince: earliest, window: TimeSpan.FromDays(7));
        var provider = new RecordingProvider();
        var checkpoints = new FakeCheckpointStore(earliest);

        var envelopes = await ReadAsync(settings, provider, new RequestBudget(10), checkpoints);

        Assert.Empty(envelopes);
        Assert.Empty(provider.Requests);
    }

    /// <summary>
    /// History gets what the live window did not spend, and sometimes that is nothing. Deliberate
    /// priority rather than an oversight: a busy day now outranks a quiet week in 2019.
    /// </summary>
    [Fact]
    public async Task ABudgetAlreadySpentOnTheLiveWindowLeavesNothingForHistory()
    {
        var settings = Settings(backfillSince: Now.AddDays(-30), window: TimeSpan.FromDays(7));
        var provider = new RecordingProvider();
        var checkpoints = new FakeCheckpointStore();

        var spent = new RequestBudget(2);
        Assert.True(spent.TrySpend());
        Assert.True(spent.TrySpend());

        await ReadAsync(settings, provider, spent, checkpoints);

        Assert.Empty(provider.Requests);
        Assert.Empty(checkpoints.Written);
    }

    private static Task<IReadOnlyList<ObservationEnvelope>> ReadAsync(
        DatasetProviderOptions settings,
        RecordingProvider provider,
        RequestBudget budget,
        FakeCheckpointStore checkpoints) =>
        DatasetBackfill.ReadAsync(
            settings,
            LiveWindowStart,
            provider.RespondAsync,
            budget,
            checkpoints,
            "test",
            NullLogger.Instance,
            CancellationToken.None);

    /// <summary>
    /// ACLED's options rather than a test double, because the type under test takes the base class
    /// and a concrete deployment is what it will actually be handed.
    /// </summary>
    private static AcledProviderOptions Settings(DateTimeOffset? backfillSince, TimeSpan? window = null)
    {
        var settings = new AcledProviderOptions { BackfillSince = backfillSince };

        if (window is { } length)
        {
            settings.BackfillWindow = length;
        }

        return settings;
    }

    private sealed class RecordingProvider(bool truncated = false)
    {
        public List<DatasetWindow> Requests { get; } = [];

        public Task<DatasetWindowResult> RespondAsync(DatasetWindow window, CancellationToken cancellationToken)
        {
            Requests.Add(window);

            ObservationEnvelope[] envelopes =
            [
                new()
                {
                    SourceName = "test",
                    Kind = ObservationKind.ExternalEvent,
                    SourceIdentifier = $"{window.Start:O}",
                    Content = "A coded event.",
                    Provenance = ObservationProvenance.Polled,
                },
            ];

            return Task.FromResult(new DatasetWindowResult(envelopes, truncated));
        }
    }

    private sealed class FakeCheckpointStore(DateTimeOffset? initial = null) : IIngestionCheckpointStore
    {
        public List<DateTimeOffset> Written { get; } = [];

        private DateTimeOffset? current = initial;

        public Task<DateTimeOffset?> ReadAsync(string source, CancellationToken cancellationToken) =>
            Task.FromResult(current);

        public Task WriteAsync(string source, DateTimeOffset requestedFrom, CancellationToken cancellationToken)
        {
            current = requestedFrom;
            Written.Add(requestedFrom);
            return Task.CompletedTask;
        }
    }
}
