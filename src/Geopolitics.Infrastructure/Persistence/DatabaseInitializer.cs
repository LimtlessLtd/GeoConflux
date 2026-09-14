using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Persistence;

public sealed partial class DatabaseInitializer(
    GeopoliticsDbContext dbContext,
    DemoDataSeeder demoDataSeeder,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var migrations = dbContext.Database.GetMigrations();

        if (migrations.Any())
        {
            await EnsureNotLegacySchemaAsync(cancellationToken);
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await EnableWriteAheadLoggingAsync(cancellationToken);

        await demoDataSeeder.SeedAsync(cancellationToken);
        LogDatabaseInitialized(logger);
    }

    /// <summary>
    /// Puts the store into write-ahead logging mode, which is what a host that stays up needs.
    /// <para>
    /// In the default rollback journal a writer excludes every reader for the length of its
    /// transaction. That is invisible in a build, which writes for a few seconds with nobody reading,
    /// and it is the shape of a stall in a host where two processor workers write while a dashboard,
    /// a hub and a health check read. Under write-ahead logging readers see the last committed state
    /// and do not wait.
    /// </para>
    /// <para>
    /// Set on every start rather than once, because the mode lives in the database file: a file
    /// restored from a backup, copied from another machine, or created before this line existed
    /// arrives in whatever mode it was in. Asking for it again costs one pragma and removes the
    /// question.
    /// </para>
    /// <para>
    /// Durability is deliberately not traded for speed alongside it. The usual companion change is
    /// <c>synchronous=NORMAL</c>, which under write-ahead logging can lose the last transactions to
    /// an operating-system crash. That is a measurable win nobody here has measured a need for, and
    /// losing the most recent reports is exactly the loss this system would notice least and mind
    /// most.
    /// </para>
    /// </summary>
    private async Task EnableWriteAheadLoggingAsync(CancellationToken cancellationToken)
    {
        // An in-memory database has no file to journal, answers "memory", and must not be asked to
        // change: the tests that use one would be changing a property the real deployment does not
        // share.
        if (dbContext.Database.GetDbConnection() is not Microsoft.Data.Sqlite.SqliteConnection connection
            || IsInMemory(connection))
        {
            return;
        }

        // Issued through the connection rather than through SqlQuery, because a pragma names its
        // own result column and EF's scalar query requires one called Value.
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        string applied;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            applied = (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? "unknown";
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }

        if (!string.Equals(applied, "wal", StringComparison.OrdinalIgnoreCase))
        {
            // Not fatal. A database on a network share cannot use it at all, and refusing to start
            // over a performance property would take the host down for a reason that is not a fault.
            LogJournalModeUnavailable(logger, applied);
        }
    }

    private static bool IsInMemory(Microsoft.Data.Sqlite.SqliteConnection connection) =>
        connection.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
        || connection.ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detects a database whose tables were created by <c>EnsureCreated</c> before this project had
    /// migrations. Such a file has the schema but no migration history, so <c>Migrate</c> would try
    /// to create tables that already exist and fail with an opaque SQLite error. Failing here with an
    /// actionable message is better than either that error or silently deleting a developer's data.
    /// </summary>
    private async Task EnsureNotLegacySchemaAsync(CancellationToken cancellationToken)
    {
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            return;
        }

        var applied = await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken);

        if (applied.Any())
        {
            return;
        }

        var tables = await dbContext.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
            .ToListAsync(cancellationToken);

        if (tables.Count == 0)
        {
            return;
        }

        var connection = dbContext.Database.GetConnectionString();
        LogLegacySchemaDetected(logger, connection ?? "the configured connection");

        throw new InvalidOperationException(
            $"The database at '{connection}' has tables but no migration history, which means it predates this project's "
            + "EF Core migrations. Delete the database file and restart to have it rebuilt from migrations.");
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The database is journalling in {JournalMode} mode rather than wal, so a writer will block readers. This is expected on a network share and unexpected anywhere else.")]
    private static partial void LogJournalModeUnavailable(ILogger logger, string journalMode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Geopolitics database initialized.")]
    private static partial void LogDatabaseInitialized(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The database for {Connection} predates migrations. Delete the database file and restart to rebuild it.")]
    private static partial void LogLegacySchemaDetected(ILogger logger, string connection);
}
