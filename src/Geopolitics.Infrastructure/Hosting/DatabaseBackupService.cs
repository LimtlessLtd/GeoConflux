using Geopolitics.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Hosting;

/// <summary>
/// Copies the database on a schedule, for as long as the host is up.
/// <para>
/// The schedule is measured from the newest copy already on disk, not from process start. A host
/// restarted twice a day would otherwise either back up twice a day or — with a naive initial
/// delay — never reach its first backup at all, and the second failure is silent.
/// </para>
/// <para>
/// It does nothing unless a destination has been named. That is the same shape every external
/// integration in this repository ships in, and here it carries an extra argument: a default
/// destination would have to be beside the database, which is a copy rather than a backup.
/// </para>
/// </summary>
public sealed partial class DatabaseBackupService(
    IServiceScopeFactory scopeFactory,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseBackupService> logger) : BackgroundService
{
    private readonly BackupOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.options.IsEnabled)
        {
            LogDisabled(logger);
            return;
        }

        var interval = this.options.Interval > TimeSpan.Zero ? this.options.Interval : TimeSpan.FromHours(24);
        LogStarting(logger, this.options.Directory, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Until(interval), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await BackUpAsync(stoppingToken);
        }
    }

    /// <summary>
    /// How long until the next copy is due, from the newest one already written.
    /// <para>
    /// Zero when one is overdue, which includes the first run on a host that has never backed up.
    /// The wait is capped at the interval so that a clock moving backwards — a correction, a restored
    /// machine — cannot park the next backup years into the future.
    /// </para>
    /// </summary>
    private TimeSpan Until(TimeSpan interval)
    {
        var existing = SqliteBackup.List(options.Directory);

        if (existing.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var newest = existing[0];

        var due = new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero) + interval;
        var wait = due - timeProvider.GetUtcNow();

        return wait <= TimeSpan.Zero ? TimeSpan.Zero : (wait > interval ? interval : wait);
    }

    private async Task BackUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

            await database.Database.OpenConnectionAsync(cancellationToken);

            try
            {
                var source = (SqliteConnection)database.Database.GetDbConnection();
                WarnIfSameVolume(source.DataSource);

                var result = SqliteBackup.Run(source, options.Directory, options.Keep, timeProvider.GetUtcNow());

                if (result.Succeeded)
                {
                    LogBackedUp(logger, result.Path!, result.Bytes, result.Removed);
                }
                else
                {
                    LogFailed(logger, result.Error ?? "unstated");
                }
            }
            finally
            {
                await database.Database.CloseConnectionAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Contained for the same reason a failed poll is: a backup that cannot be taken is a
            // degraded cycle, not a reason to stop serving the dashboard. The next one tries again.
            LogFailedWithException(logger, exception);
        }
    }

    /// <summary>
    /// Says so when the copies are going to the same volume as the original.
    /// <para>
    /// Not refused, because it is a real and useful arrangement: it survives a bad write, a bad
    /// migration, and a mistaken delete. It does not survive the failure most people mean by the
    /// word, and a deployment should be told which of the two it has.
    /// </para>
    /// </summary>
    private void WarnIfSameVolume(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        var database = SqliteBackup.VolumeOf(databasePath);
        var backups = SqliteBackup.VolumeOf(options.Directory);

        if (string.Equals(database, backups, StringComparison.OrdinalIgnoreCase))
        {
            LogSameVolume(logger, backups);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No backup destination is configured, so no copies of the database will be taken. Set Backup:Directory to turn this on.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database backups go to {Directory} every {Interval}.")]
    private static partial void LogStarting(ILogger logger, string directory, TimeSpan interval);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database copied to {Path} ({Bytes} bytes); {Removed} older copy(ies) rotated out.")]
    private static partial void LogBackedUp(ILogger logger, string path, long bytes, int removed);

    [LoggerMessage(Level = LogLevel.Error, Message = "The database backup did not complete: {Reason}. Nothing was rotated out.")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The database backup failed. The next scheduled attempt is unaffected.")]
    private static partial void LogFailedWithException(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Backups are being written to {Volume}, the same volume as the database. That survives a bad write and a mistaken delete; it does not survive a failed disk.")]
    private static partial void LogSameVolume(ILogger logger, string volume);
}
