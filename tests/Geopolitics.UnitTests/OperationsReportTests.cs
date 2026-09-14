using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Operations;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// What the host is allowed to say about its own size.
/// <para>
/// The sprint that added this attached a rule to it: measure before deciding. These assert the two
/// places where that rule has teeth — a projection that declines rather than extrapolating from an
/// hour, and a table nobody described still being counted rather than quietly dropped from a figure
/// whose whole job is to be total.
/// </para>
/// </summary>
public sealed class OperationsReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RowCountsAreOrderedLargestFirst()
    {
        // The order is a decision about what a retention question is about, not a presentation
        // preference: the largest table is the only one the answer turns on.
        var report = await BuildAsync(Measurement() with
        {
            Tables =
            [
                new TableRowCount("incidents", 40),
                new TableRowCount("observations", 900),
                new TableRowCount("ai_inferences", 120),
            ],
        });

        Assert.Equal(
            ["observations", "ai_inferences", "incidents"],
            report.Holdings.Tables.Select(table => table.Table));
    }

    [Fact]
    public async Task ATableNobodyDescribedIsStillCounted()
    {
        // A table added by a later migration must appear the day it exists. Omitting it would make
        // the total quietly wrong, and a total that excludes something is worse than no total.
        var report = await BuildAsync(Measurement() with
        {
            Tables = [new TableRowCount("something_added_later", 7)],
        });

        var table = Assert.Single(report.Holdings.Tables);
        Assert.Equal(7, table.Rows);
        Assert.Equal("not described here", table.Holds);
    }

    [Fact]
    public async Task GrowthIsNotProjectedFromLessThanAWeekOfRecords()
    {
        var report = await BuildAsync(Measurement() with
        {
            OldestReceivedAt = Now.AddHours(-3),
            NewestReceivedAt = Now,
            ObservationsLastWeek = 24,
        });

        // The refusal is the point. Multiplying three hours by 2,920 produces a figure carrying a
        // year's authority and three hours' support, and it is the figure that would get quoted.
        Assert.Null(report.Holdings.Growth.ProjectedYearBytes);
        Assert.Contains("less than seven days", report.Holdings.Growth.Basis, StringComparison.Ordinal);

        // It still reports what it measured. Declining to project is not declining to say anything.
        Assert.Equal(24, report.Holdings.Growth.ObservationsLastWeek);
    }

    [Fact]
    public async Task GrowthIsProjectedOnceThereIsAWeekToProjectFrom()
    {
        var report = await BuildAsync(Measurement() with
        {
            DatabaseBytes = 1_000_000,
            Tables = [new TableRowCount("observations", 1_000)],
            OldestReceivedAt = Now.AddDays(-30),
            NewestReceivedAt = Now,
            ObservationsLastWeek = 700,
        });

        // 1,000 bytes an observation, 700 a week, 52.14 weeks: a shade over 36 MB.
        Assert.NotNull(report.Holdings.Growth.ProjectedYearBytes);
        Assert.InRange(report.Holdings.Growth.ProjectedYearBytes!.Value, 36_000_000, 36_600_000);
        Assert.Equal(1_000, report.Holdings.Growth.BytesPerObservation);
    }

    [Fact]
    public async Task AnEmptyDatabaseProjectsNothingRatherThanDividingByZero()
    {
        var report = await BuildAsync(Measurement() with
        {
            Tables = [new TableRowCount("observations", 0)],
            OldestReceivedAt = null,
            NewestReceivedAt = null,
        });

        Assert.Null(report.Holdings.Growth.ProjectedYearBytes);
        Assert.Equal(0, report.Holdings.Growth.BytesPerObservation);
    }

    [Fact]
    public async Task AJournalModeOtherThanWalIsNamedAsTheWrongOneForAHostLeftRunning()
    {
        var rollback = await BuildAsync(Measurement() with { JournalMode = "delete" });
        Assert.Contains("wants wal", rollback.Holdings.Storage.Note, StringComparison.Ordinal);

        var wal = await BuildAsync(Measurement() with { JournalMode = "wal" });
        Assert.Contains("do not block each other", wal.Holdings.Storage.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreedPagesAreReportedAsNeedingAVacuumToComeBack()
    {
        var report = await BuildAsync(Measurement() with { ReclaimableBytes = 4_096 });

        Assert.Equal(4_096, report.Holdings.Storage.ReclaimableBytes);
        Assert.Contains("vacuum", report.Holdings.Storage.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostWithNoBackupDestinationIsToldThatNothingWouldSurviveTheDisk()
    {
        var report = await BuildAsync(Measurement());

        Assert.False(report.Backups.Configured);
        Assert.Contains("would survive the loss of this database", report.Backups.Note, StringComparison.Ordinal);

        // There is no default destination, and the reason is said rather than left implicit: the
        // only possible default is beside the original, which is a copy and not a backup.
        Assert.Contains("no default", report.Backups.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADestinationWithNothingInItIsCalledASchedule()
    {
        var report = await BuildAsync(Measurement() with
        {
            Backups = new BackupState(Configured: true, 0, null, 0, SameVolumeAsDatabase: false),
        });

        Assert.Contains("schedule rather than a backup", report.Backups.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopiesOnTheSameVolumeAreNamedAsSurvivingAMistakeAndNotADisk()
    {
        // The arrangement a deployment is most likely to end up with without having chosen it, and
        // the one that most looks like the problem is solved.
        var report = await BuildAsync(Measurement() with
        {
            Backups = new BackupState(true, 7, Now.AddHours(-2), 4_096_000, SameVolumeAsDatabase: true),
        });

        Assert.Equal(7, report.Backups.Copies);
        Assert.Contains("not a failed disk", report.Backups.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackupThatStoppedRunningIsNamedRatherThanCounted()
    {
        // Seven copies and the newest a fortnight old reads as healthy from the count alone. The
        // count is exactly what a failing schedule leaves looking right.
        var report = await BuildAsync(Measurement() with
        {
            Backups = new BackupState(true, 7, Now.AddDays(-14), 4_096_000, SameVolumeAsDatabase: false),
        });

        Assert.Contains("14 days old", report.Backups.Note, StringComparison.Ordinal);
        Assert.Contains("not running", report.Backups.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionStatesWhatItWillNeverDeleteBesideWhatItHas()
    {
        var report = await BuildAsync(Measurement() with
        {
            Retention = new RetentionState(Enabled: true, TimeSpan.FromDays(90), 12, 400, 1_048_576, Now.AddHours(-6)),
        });

        // The boundary travels with the numbers rather than living only in an ADR. A reader looking
        // at four hundred deleted rows is entitled to know in the same breath that no incident lost
        // its evidence to them.
        Assert.Contains("never touched", report.Retention.Boundary, StringComparison.Ordinal);
        Assert.Contains("held for corroboration", report.Retention.Boundary, StringComparison.Ordinal);
        Assert.Equal(400, report.Retention.RowsRemoved);
        Assert.Equal(12, report.Retention.PrunableNow);
    }

    [Fact]
    public async Task ThePrunableCountIsReportedEvenWithRetentionOff()
    {
        // Off with nothing prunable and off while sitting on a hundred thousand prunable rows are
        // different situations, and the count is the only thing that separates them.
        var report = await BuildAsync(Measurement() with
        {
            Retention = new RetentionState(Enabled: false, TimeSpan.FromDays(90), 100_000, 0, 0, null),
        });

        Assert.False(report.Retention.Enabled);
        Assert.Equal(100_000, report.Retention.PrunableNow);
        Assert.Contains("nothing is deleted", report.Retention.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionTurnedOnButNotYetRunSaysSoRatherThanReadingAsZeroDeleted()
    {
        var report = await BuildAsync(Measurement() with
        {
            Retention = new RetentionState(Enabled: true, TimeSpan.FromDays(90), 12, 0, 0, null),
        });

        Assert.Contains("has not yet run", report.Retention.Note, StringComparison.Ordinal);
    }

    private static DatabaseMeasurement Measurement() => new(
        [new TableRowCount("observations", 24)],
        DatabaseBytes: 409_600,
        JournalBytes: 0,
        ReclaimableBytes: 0,
        JournalMode: "wal",
        OldestReceivedAt: Now.AddDays(-1),
        NewestReceivedAt: Now,
        Backups: new BackupState(Configured: false, 0, null, 0, SameVolumeAsDatabase: false),
        Retention: new RetentionState(Enabled: false, TimeSpan.FromDays(90), 0, 0, 0, null),
        ObservationsLastWeek: 24);

    private static async Task<OperationsReport> BuildAsync(DatabaseMeasurement measurement)
    {
        var service = new OperationsService(new StubRepository(measurement), new FakeTimeProvider(Now));
        return await service.BuildAsync(CancellationToken.None);
    }

    private sealed class StubRepository(DatabaseMeasurement measurement) : IOperationsRepository
    {
        public Task<DatabaseMeasurement> MeasureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(measurement);
    }
}
