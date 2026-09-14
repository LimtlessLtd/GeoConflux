namespace Geopolitics.Application.Abstractions;

/// <param name="Table">The table's name in the database, not the class name in this solution.</param>
/// <param name="Rows">How many rows it holds.</param>
public sealed record TableRowCount(string Table, long Rows);

/// <summary>
/// What the store can say about its own size, before anybody decides what to do about it.
/// </summary>
/// <param name="Tables">Row counts, one per mapped table.</param>
/// <param name="DatabaseBytes">
/// The database's own size — pages times page size. This is the figure that grows, and it is not the
/// same as the size of the file on disk after a delete, which is why the two are reported separately.
/// </param>
/// <param name="JournalBytes">
/// The write-ahead log and its shared-memory index. Transient by nature: a checkpoint returns this
/// space to the database file, so a large value here is a statement about recent write volume rather
/// than about how much is stored.
/// </param>
/// <param name="ReclaimableBytes">
/// Pages the database has freed and is holding for reuse. Deleting rows moves space here; only a
/// vacuum returns it to the filesystem. This is the number that says whether a vacuum is worth its
/// cost.
/// </param>
/// <param name="JournalMode">
/// How the store journals. A long-running host wants <c>wal</c>, which is what lets a reader and a
/// writer proceed at the same time instead of blocking each other.
/// </param>
/// <param name="OldestReceivedAt">When the earliest surviving observation arrived, or null if there are none.</param>
/// <param name="NewestReceivedAt">When the latest one arrived.</param>
/// <param name="Backups">What copies of this database exist, if any.</param>
/// <param name="ObservationsLastWeek">
/// How many arrived in the last seven days. The only honest basis for projecting growth, because it
/// is a measurement of this deployment rather than an assumption about a different one.
/// </param>
public sealed record DatabaseMeasurement(
    IReadOnlyList<TableRowCount> Tables,
    long DatabaseBytes,
    long JournalBytes,
    long ReclaimableBytes,
    string JournalMode,
    DateTimeOffset? OldestReceivedAt,
    DateTimeOffset? NewestReceivedAt,
    BackupState Backups,
    long ObservationsLastWeek);

/// <summary>
/// What copies of this database exist, read from the destination rather than from what a scheduler
/// believes it did.
/// <para>
/// The distinction is the whole value of the figure. A service that has been failing for a fortnight
/// reports a fortnight of attempts; the directory reports a fortnight-old copy, which is the fact an
/// operator needs and the one a status flag would have hidden.
/// </para>
/// </summary>
/// <param name="Configured">Whether a destination has been named at all.</param>
/// <param name="Copies">How many copies are on hand.</param>
/// <param name="NewestAt">When the most recent one was written, or null if there are none.</param>
/// <param name="NewestBytes">How large that one is.</param>
/// <param name="SameVolumeAsDatabase">
/// Whether the copies sit on the same volume as the original, as far as this host can tell. True is
/// not a fault, and it is not a backup in the sense most deployments mean.
/// </param>
public sealed record BackupState(
    bool Configured,
    int Copies,
    DateTimeOffset? NewestAt,
    long NewestBytes,
    bool SameVolumeAsDatabase);

/// <summary>
/// Measures the store this deployment is actually running against.
/// <para>
/// It belongs behind an interface for the ordinary reason — the Application layer does not know what
/// SQLite is — but also because most of what it returns is not a query at all. Page counts, free
/// pages and journal mode are properties of the storage engine, and any other engine would answer
/// them differently or not at all.
/// </para>
/// </summary>
public interface IOperationsRepository
{
    Task<DatabaseMeasurement> MeasureAsync(CancellationToken cancellationToken);
}
