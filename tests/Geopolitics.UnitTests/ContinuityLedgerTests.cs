using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// Working out whether this host has been away.
/// <para>
/// The only evidence available is that an adapter asked a provider something at a given moment,
/// because that is the one thing that can only be true of a running process. Observations carry the
/// times sources reported, not the times this host was alive, and a backfill routinely stores records
/// dated years ago.
/// </para>
/// <para>
/// Which makes the first test the important one. No polls on record is <em>no evidence</em>, not
/// <em>no downtime</em>, and a ledger that conflated them would report a machine that had been off
/// for a month as having run continuously.
/// </para>
/// </summary>
public sealed class ContinuityLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AHostWithNoPollsOnRecordRecordsNothingRatherThanNoDowntime()
    {
        var repository = new StubRepository([]);

        Assert.Null(await Ledger(repository).RecordStartupGapAsync(CancellationToken.None));
        Assert.Empty(repository.Written);
    }

    [Fact]
    public async Task AGapInsideOrdinaryPollingCadenceIsNotDowntime()
    {
        // One interval's silence is a poll that ran late, a provider that took a minute, or a clock
        // that moved. Recording those would fill the ledger with noise that Sprint 18 would then go
        // and re-request a fortnight of history for.
        var repository = new StubRepository(
            [new SourceLiveness("rss", Now.AddMinutes(-20), TimeSpan.FromMinutes(15))]);

        Assert.Null(await Ledger(repository).RecordStartupGapAsync(CancellationToken.None));
        Assert.Empty(repository.Written);
    }

    [Fact]
    public async Task ARestartWithinMinutesIsNotAnOutage()
    {
        // A fast feed would otherwise report every deployment as downtime. Two intervals of a
        // one-minute poll is two minutes, and nobody means that by "the host was down".
        var repository = new StubRepository(
            [new SourceLiveness("rss", Now.AddMinutes(-4), TimeSpan.FromMinutes(1))]);

        Assert.Null(await Ledger(repository).RecordStartupGapAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AGapLongerThanTwoPollingIntervalsIsRecordedFromTheLastPoll()
    {
        var lastPoll = Now.AddDays(-14);
        var repository = new StubRepository([new SourceLiveness("rss", lastPoll, TimeSpan.FromMinutes(15))]);

        var period = await Ledger(repository).RecordStartupGapAsync(CancellationToken.None);

        Assert.NotNull(period);

        // From the last known poll rather than one interval after it. Over-covering costs a re-read
        // that deduplication absorbs; under-covering loses records nothing will ask for again.
        Assert.Equal(lastPoll, period!.StartedAt);
        Assert.Equal(Now, period.EndedAt);
        Assert.Equal("rss", period.LastSource);
        Assert.Equal(TimeSpan.FromDays(14), period.Duration);
        Assert.Single(repository.Written);
    }

    [Fact]
    public async Task TheNewestPollAcrossAllSourcesDecidesWhenTheHostWasLastAlive()
    {
        // Any source polling proves the process was up, so the freshest one is the answer — and the
        // fastest cadence among them is the resolution, because that is the one that would have
        // fired first had the host been running.
        var repository = new StubRepository(
        [
            new SourceLiveness("ucdp", Now.AddDays(-30), TimeSpan.FromHours(24)),
            new SourceLiveness("rss", Now.AddDays(-2), TimeSpan.FromMinutes(15)),
        ]);

        var period = await Ledger(repository).RecordStartupGapAsync(CancellationToken.None);

        Assert.NotNull(period);
        Assert.Equal("rss", period!.LastSource);
        Assert.Equal(Now.AddDays(-2), period.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(15), period.Cadence);
    }

    [Fact]
    public async Task ASlowDatasetAloneCannotDetectAShortOutage()
    {
        // Stated rather than papered over: with only a daily poll on record, a day off the air is
        // indistinguishable from having been up the whole time. That is the honest limit of
        // inferring uptime from polls, and it is why the report publishes the resolution.
        var repository = new StubRepository(
            [new SourceLiveness("ucdp", Now.AddHours(-30), TimeSpan.FromHours(24))]);

        Assert.Null(await Ledger(repository).RecordStartupGapAsync(CancellationToken.None));
    }

    private static ContinuityService Ledger(IContinuityRepository repository) =>
        new(repository, new FakeTimeProvider(Now), NullLogger<ContinuityService>.Instance);

    private sealed class StubRepository(IReadOnlyList<SourceLiveness> liveness) : IContinuityRepository
    {
        public List<DowntimeRecord> Written { get; } = [];

        public Task<IReadOnlyList<SourceLiveness>> ReadLivenessAsync(CancellationToken cancellationToken) =>
            Task.FromResult(liveness);

        public Task AddDowntimeAsync(DowntimeRecord period, CancellationToken cancellationToken)
        {
            Written.Add(period);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DowntimeRecord>> ListDowntimeAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DowntimeRecord>>(Written);
    }
}
