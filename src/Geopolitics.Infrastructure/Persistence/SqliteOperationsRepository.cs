using Geopolitics.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// Measures the SQLite database this deployment is running against.
/// <para>
/// Deliberately not written in LINQ. Row counts could be, but page counts, free pages and journal
/// mode are properties of the storage engine rather than of the model, and there is no provider-
/// neutral way to ask for them — which is the honest reason this whole interface exists rather than
/// the counts being tacked onto the coverage repository.
/// </para>
/// <para>
/// Tables are enumerated from the EF model rather than listed here. A table added by a later
/// migration then appears in the report the day it exists, instead of being silently missing from a
/// figure whose entire job is to be complete.
/// </para>
/// </summary>
public sealed class SqliteOperationsRepository(GeopoliticsDbContext dbContext, TimeProvider timeProvider)
    : IOperationsRepository
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    public async Task<DatabaseMeasurement> MeasureAsync(CancellationToken cancellationToken)
    {
        // Opened through the facade rather than through the connection, so the reference count EF
        // keeps stays correct and this does not close a connection somebody else is using.
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = (SqliteConnection)dbContext.Database.GetDbConnection();

            var pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
            var pageCount = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);
            var freePages = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken);
            var journalMode = await TextAsync(connection, "PRAGMA journal_mode;", cancellationToken);

            var tables = new List<TableRowCount>();

            foreach (var table in MappedTables())
            {
                tables.Add(new TableRowCount(
                    table,
                    await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{table}\";", cancellationToken)));
            }

            var cutoff = timeProvider.GetUtcNow() - RecentWindow;
            var observations = dbContext.Observations.AsNoTracking();

            return new DatabaseMeasurement(
                tables,
                pageSize * pageCount,
                JournalBytes(connection),
                pageSize * freePages,
                journalMode,
                await observations.MinAsync(value => (DateTimeOffset?)value.ReceivedAt, cancellationToken),
                await observations.MaxAsync(value => (DateTimeOffset?)value.ReceivedAt, cancellationToken),
                await observations.LongCountAsync(value => value.ReceivedAt >= cutoff, cancellationToken));
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Every table the model maps, taken from the model itself.
    /// <para>
    /// Owned types are excluded because they share their owner's table: counting them would report
    /// the observations table twice, once under a name no database contains.
    /// </para>
    /// </summary>
    private IEnumerable<string> MappedTables() =>
        dbContext.Model.GetEntityTypes()
            .Where(entity => !entity.IsOwned())
            .Select(entity => entity.GetTableName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>
    /// The write-ahead log and its shared-memory index, measured on disk.
    /// <para>
    /// There is no pragma for this: the log is a separate file, and SQLite reports the database's
    /// size without it. An in-memory database has no files at all, which is nought rather than an
    /// error — the tests run that way.
    /// </para>
    /// </summary>
    private static long JournalBytes(SqliteConnection connection)
    {
        var path = connection.DataSource;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 0;
        }

        return Length(path + "-wal") + Length(path + "-shm");

        static long Length(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> TextAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value?.ToString() ?? "unknown";
    }
}
