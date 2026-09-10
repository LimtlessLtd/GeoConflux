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

        await demoDataSeeder.SeedAsync(cancellationToken);
        LogDatabaseInitialized(logger);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Geopolitics database initialized.")]
    private static partial void LogDatabaseInitialized(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The database for {Connection} predates migrations. Delete the database file and restart to rebuild it.")]
    private static partial void LogLegacySchemaDetected(ILogger logger, string connection);
}
