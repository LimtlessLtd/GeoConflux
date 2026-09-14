using Geopolitics.Application.Abstractions;

namespace Geopolitics.Application.Operations;

/// <param name="Table">The table's name in the database.</param>
/// <param name="Rows">How many rows it holds.</param>
/// <param name="Holds">What those rows are, in one phrase, so a reader need not know the schema.</param>
public sealed record TableHolding(string Table, long Rows, string Holds);

/// <param name="DatabaseBytes">The database's own size.</param>
/// <param name="JournalBytes">The write-ahead log and its index, which a checkpoint returns.</param>
/// <param name="ReclaimableBytes">Freed pages a vacuum would return to the filesystem.</param>
/// <param name="JournalMode">How the store journals; a long-running host wants <c>wal</c>.</param>
/// <param name="Note">What these figures do and do not mean, said beside them.</param>
public sealed record StorageFootprint(
    long DatabaseBytes,
    long JournalBytes,
    long ReclaimableBytes,
    string JournalMode,
    string Note);

/// <summary>
/// What this database would cost at the rate it is currently filling.
/// </summary>
/// <param name="ObservationsLastWeek">Observations received in the last seven days.</param>
/// <param name="BytesPerObservation">
/// The database's size divided by the observations in it. A crude figure and the right one: it
/// carries the incidents, inferences and indexes each observation drags along with it, which a
/// row-size estimate would miss.
/// </param>
/// <param name="ProjectedYearBytes">
/// What a year at last week's rate would add. Null when there is not enough history to say — which
/// is the honest answer on a host that started this morning, and is what a made-up number hides.
/// </param>
/// <param name="Basis">What this projection assumes, stated so it is read as a projection.</param>
public sealed record GrowthEstimate(
    long ObservationsLastWeek,
    double BytesPerObservation,
    long? ProjectedYearBytes,
    string Basis);

/// <summary>
/// What this host holds, and how fast it is filling up.
/// </summary>
/// <param name="Tables">Row counts, largest first, because that is the order a retention decision reads them in.</param>
/// <param name="Storage">Bytes: stored, journalled, and reclaimable.</param>
/// <param name="OldestRecordAt">When the earliest surviving observation arrived.</param>
/// <param name="NewestRecordAt">When the latest one did.</param>
/// <param name="Growth">What a year at the current rate would add.</param>
/// <param name="Note">What this whole section is for.</param>
public sealed record HoldingsReport(
    IReadOnlyList<TableHolding> Tables,
    StorageFootprint Storage,
    DateTimeOffset? OldestRecordAt,
    DateTimeOffset? NewestRecordAt,
    GrowthEstimate Growth,
    string Note);

/// <summary>
/// Whether this database is recoverable, and from what.
/// </summary>
/// <param name="Configured">Whether a destination has been named at all.</param>
/// <param name="Copies">How many copies are on hand.</param>
/// <param name="NewestAt">When the most recent one was written.</param>
/// <param name="NewestBytes">How large it is.</param>
/// <param name="Note">
/// What those copies are worth, in a sentence. The important case is a destination on the same
/// volume as the database, which is a real safeguard against a bad write and no safeguard at all
/// against a failed disk — and is the arrangement a deployment is most likely to have without
/// having decided to.
/// </param>
public sealed record BackupStanding(
    bool Configured,
    int Copies,
    DateTimeOffset? NewestAt,
    long NewestBytes,
    string Note);

/// <summary>
/// What a host that has been left running can say about itself.
/// <para>
/// Separate from the coverage report, which answers a different question. Coverage is about the
/// world — how much of it this system reached. This is about the machine — what it is holding, and
/// how long it has been holding it. They are shown together because the second is a constraint on
/// the first, and because a reader deciding whether to trust a thin panel deserves to know whether
/// the host producing it has been up for a month or for ninety seconds.
/// </para>
/// </summary>
/// <param name="Holdings">Rows and bytes, measured rather than estimated.</param>
/// <param name="Backups">Whether any of it would survive the disk it is on.</param>
/// <param name="MeasuredAt">When these figures were taken.</param>
public sealed record OperationsReport(
    HoldingsReport Holdings,
    BackupStanding Backups,
    DateTimeOffset MeasuredAt);

/// <summary>States what this host holds and how it has been running.</summary>
public interface IOperationsService
{
    Task<OperationsReport> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Turns the store's own measurements into statements a retention decision can be made against.
/// <para>
/// This exists because the sprint that introduced it had a rule attached: measure before deciding.
/// No retention policy in this repository was chosen against a number, because until now there was
/// no number — and a policy picked from a guess about volume would be wrong in whichever direction
/// the guess was wrong, silently, for months.
/// </para>
/// </summary>
public sealed class OperationsService(IOperationsRepository repository, TimeProvider timeProvider) : IOperationsService
{
    /// <summary>
    /// Seven days, because that is the shortest span over which a weekly-cyclical reporting rate
    /// averages out. A single day would project a quiet Sunday across a whole year.
    /// </summary>
    private static readonly TimeSpan ProjectionBasis = TimeSpan.FromDays(7);

    /// <summary>
    /// A plain phrase for each table.
    /// <para>
    /// Matched by name rather than by type so that a table added later still appears, described as
    /// undescribed rather than omitted. An unlisted table would be invisible in exactly the count
    /// that is meant to be total, and a total that quietly excludes something is worse than no total.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Describes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["observations"] = "what sources actually said, including duplicates and failures",
        ["incidents"] = "what this system concluded from them",
        ["ai_inferences"] = "the audit trail of every model call, successful or not",
        ["IngestionCheckpoints"] = "how far back each adapter has asked, and when it last polled",
    };

    public async Task<OperationsReport> BuildAsync(CancellationToken cancellationToken)
    {
        var measurement = await repository.MeasureAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var tables = measurement.Tables
            .OrderByDescending(table => table.Rows)
            .ThenBy(table => table.Table, StringComparer.Ordinal)
            .Select(table => new TableHolding(
                table.Table,
                table.Rows,
                Describes.TryGetValue(table.Table, out var describes) ? describes : "not described here"))
            .ToList();

        var observations = measurement.Tables
            .FirstOrDefault(table => string.Equals(table.Table, "observations", StringComparison.OrdinalIgnoreCase))
            ?.Rows ?? 0;

        return new OperationsReport(
            new HoldingsReport(
                tables,
                new StorageFootprint(
                    measurement.DatabaseBytes,
                    measurement.JournalBytes,
                    measurement.ReclaimableBytes,
                    measurement.JournalMode,
                    StorageNote(measurement)),
                measurement.OldestReceivedAt,
                measurement.NewestReceivedAt,
                Project(measurement, observations, now),
                "Rows and bytes this host is holding now. A retention policy is a decision about "
                + "these numbers, and until they existed there was nothing to decide it against."),
            Standing(measurement.Backups, now),
            now);
    }

    /// <summary>
    /// States what the copies on hand are worth.
    /// <para>
    /// Three cases and they are genuinely different. No destination named is a deployment that has
    /// not decided. A destination with nothing in it is a schedule that has not run or has been
    /// failing. And a destination on the same volume is the one that looks like the answer and is
    /// not — it survives a bad write and a mistaken delete, and not the disk.
    /// </para>
    /// </summary>
    private static BackupStanding Standing(BackupState backups, DateTimeOffset now)
    {
        if (!backups.Configured)
        {
            return new BackupStanding(
                false,
                0,
                null,
                0,
                "No backup destination is configured, so nothing here would survive the loss of this "
                + "database. Naming a destination is what turns backups on; there is no default, "
                + "because the only possible default would be beside the original.");
        }

        if (backups.NewestAt is not { } newest)
        {
            return new BackupStanding(
                true,
                0,
                null,
                0,
                "A destination is configured and holds no copies yet. Until one appears this is a "
                + "schedule rather than a backup.");
        }

        var age = now - newest;
        var stale = age > TimeSpan.FromDays(2)
            ? $" The newest is {(int)age.TotalDays} days old, which is a schedule that is not running."
            : string.Empty;

        var volume = backups.SameVolumeAsDatabase
            ? " They are on the same volume as the database, so they survive a bad write or a "
                + "mistaken delete and not a failed disk."
            : " They are on a different volume from the database.";

        return new BackupStanding(true, backups.Copies, newest, backups.NewestBytes, $"{backups.Copies} copy(ies) on hand.{volume}{stale}");
    }

    /// <summary>
    /// Projects a year forward, or declines to.
    /// <para>
    /// The refusal is the part worth keeping. A host that has been up for an hour has one hour of
    /// evidence, and multiplying it by 8,760 produces a figure carrying a year's authority and an
    /// hour's support. It is the same rule the narrative evidence floor applies to prose, applied
    /// here to arithmetic.
    /// </para>
    /// </summary>
    private static GrowthEstimate Project(DatabaseMeasurement measurement, long observations, DateTimeOffset now)
    {
        var bytesPerObservation = observations > 0 ? (double)measurement.DatabaseBytes / observations : 0;

        // A week of records, not a week of uptime. A host restarted twice yesterday still holds
        // seven days of history if it has been ingesting for seven days, and that is what the rate
        // is computed from.
        var history = measurement.OldestReceivedAt is { } oldest ? now - oldest : TimeSpan.Zero;

        if (history < ProjectionBasis || observations == 0)
        {
            return new GrowthEstimate(
                measurement.ObservationsLastWeek,
                bytesPerObservation,
                null,
                "Not projected. This database holds less than seven days of records, which is too "
                + "little to tell a rate from a start-up burst.");
        }

        var weeksPerYear = 365.0 / ProjectionBasis.TotalDays;
        var perYear = (long)(measurement.ObservationsLastWeek * weeksPerYear * bytesPerObservation);

        return new GrowthEstimate(
            measurement.ObservationsLastWeek,
            bytesPerObservation,
            perYear,
            "A year at the last seven days' rate, at this database's current bytes per observation. "
            + "It projects this deployment's configuration rather than the world: turning on a "
            + "dataset credential changes it by orders of magnitude on the day it is turned on.");
    }

    private static string StorageNote(DatabaseMeasurement measurement)
    {
        var journal = string.Equals(measurement.JournalMode, "wal", StringComparison.OrdinalIgnoreCase)
            ? "The store is in write-ahead logging mode, so a reader and a writer do not block each other."
            : $"The store journals in {measurement.JournalMode} mode, which blocks readers against a writer. "
                + "A continuously-running host wants wal.";

        var reclaimable = measurement.ReclaimableBytes > 0
            ? " Freed pages are held for reuse; only a vacuum returns them to the filesystem."
            : " There are no freed pages to return.";

        return journal + reclaimable;
    }
}
