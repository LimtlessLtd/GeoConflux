using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Operations;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The downtime ledger against the real schema, where the two facts it keeps share a row.
/// <para>
/// A poll and a backfill both write to <c>IngestionCheckpoints</c> and they mean different things:
/// how far back history has been requested only ever moves earlier, and when this host last polled
/// only ever moves later. The second test is the one that matters, because getting it wrong is
/// silent — a poll that filled in a backfill frontier would tell the walk that history from that
/// moment had already been asked for, and it would start short of where it should and never notice.
/// </para>
/// </summary>
public sealed class DowntimeLedgerTests
{
    [Fact]
    public async Task AGapBetweenOneRunAndTheNextIsRecordedAsAPeriodTheHostWasAway()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var liveness = factory.Services.GetRequiredService<ISourceLivenessRecorder>();
        await liveness.RecordPollAsync("rss", TimeSpan.FromMinutes(15), CancellationToken.None);

        // Stand in for the previous run having stopped a fortnight ago. There is no way to fake this
        // from outside the database, which is the point: the ledger's whole input is one timestamp.
        var away = DateTimeOffset.UtcNow - TimeSpan.FromDays(14);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();
            await database.Checkpoints.ExecuteUpdateAsync(
                update => update.SetProperty(checkpoint => checkpoint.LastPolledAt, away),
                CancellationToken.None);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var continuity = scope.ServiceProvider.GetRequiredService<IContinuityService>();
            var period = await continuity.RecordStartupGapAsync(CancellationToken.None);

            Assert.NotNull(period);
            Assert.Equal("rss", period!.LastSource);
            Assert.InRange(period.Duration, TimeSpan.FromDays(13.9), TimeSpan.FromDays(14.1));

            // Durable, because Sprint 18 has to act on it after this process is gone.
            var stored = await continuity.RecentAsync(10, CancellationToken.None);
            Assert.Single(stored);
            Assert.Equal(period.Id, stored[0].Id);
        }
    }

    [Fact]
    public async Task RecordingAPollDoesNotInventABackfillFrontier()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var liveness = factory.Services.GetRequiredService<ISourceLivenessRecorder>();
        await liveness.RecordPollAsync("acled", TimeSpan.FromHours(6), CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

        var checkpoint = await database.Checkpoints
            .AsNoTracking()
            .SingleAsync(value => value.Source == "acled", CancellationToken.None);

        Assert.NotNull(checkpoint.LastPolledAt);
        Assert.Equal(TimeSpan.FromHours(6), checkpoint.PollEvery);

        // The row exists because a poll happened, and it must not answer a question the poll did not
        // ask. A frontier here would have the backfill believe history from this moment had already
        // been requested, and the walk would stop short of where it should with nothing to show it.
        Assert.Null(checkpoint.RequestedFrom);
        Assert.Null(checkpoint.UpdatedAt);
    }

    [Fact]
    public async Task AHostThatHasNeverPolledRecordsNoDowntimeAndSaysWhy()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        var continuity = scope.ServiceProvider.GetRequiredService<IContinuityService>();
        Assert.Null(await continuity.RecordStartupGapAsync(CancellationToken.None));

        var operations = await scope.ServiceProvider.GetRequiredService<IOperationsService>()
            .BuildAsync(CancellationToken.None);

        // No evidence either way, reported as exactly that. "No downtime" here would be the same
        // error as an empty map reading as peace, which is the one this dashboard exists to avoid.
        Assert.False(operations.Downtime.Measurable);
        Assert.Empty(operations.Downtime.Periods);
        Assert.Contains(
            "not a statement that it has always been up",
            operations.Downtime.Note,
            StringComparison.Ordinal);
    }
}
