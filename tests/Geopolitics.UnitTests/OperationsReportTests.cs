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

    private static DatabaseMeasurement Measurement() => new(
        [new TableRowCount("observations", 24)],
        DatabaseBytes: 409_600,
        JournalBytes: 0,
        ReclaimableBytes: 0,
        JournalMode: "wal",
        OldestReceivedAt: Now.AddDays(-1),
        NewestReceivedAt: Now,
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
