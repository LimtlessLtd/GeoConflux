using Geopolitics.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Geopolitics.UnitTests;

/// <summary>
/// Backing up a database that is being written to.
/// <para>
/// The first test is the reason this code exists. In write-ahead logging mode a committed row can
/// live in the log rather than in the database file, so the obvious implementation — copy the file —
/// produces a copy that opens cleanly and is missing data. It does not fail, it does not warn, and
/// nobody finds out until they restore it. The test therefore does both: it takes the naive copy and
/// the proper one from the same instant and compares what each one holds.
/// </para>
/// </summary>
public sealed class SqliteBackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"geoconflux-backup-tests-{Guid.NewGuid():N}");

    private string DatabasePath => Path.Combine(directory, "source.db");

    private string BackupDirectory => Path.Combine(directory, "backups");

    public SqliteBackupTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ACopyOfTheFileLosesWhatTheWriteAheadLogIsStillHolding()
    {
        using var source = OpenWriteAheadDatabase(rows: 50);

        // The premise of the whole test: there is genuinely committed data outside the main file.
        var log = new FileInfo(DatabasePath + "-wal");
        Assert.True(log.Exists && log.Length > 0, "the write-ahead log should hold the committed rows");

        var naive = Path.Combine(directory, "naive-copy.db");
        CopyFile(DatabasePath, naive);

        var result = SqliteBackup.Run(source, BackupDirectory, keep: 7, Now);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(50, CountRows(result.Path!));

        // And the copy anybody would have written by hand does not hold them.
        Assert.True(
            CountRows(naive) < 50,
            "a file copy of a database in write-ahead mode should be missing rows the log still holds; "
            + "if this ever stops being true the backup could be simplified, but it is true today.");
    }

    [Fact]
    public void ACopyIsVerifiedBeforeAnythingIsRotatedOut()
    {
        using var source = OpenWriteAheadDatabase(rows: 5);

        // Written under a partial name and renamed on success, so an interrupted run leaves nothing
        // a rotation would count as a backup.
        var result = SqliteBackup.Run(source, BackupDirectory, keep: 7, Now);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(Directory.GetFiles(BackupDirectory, "*.partial"));
        Assert.Single(SqliteBackup.List(BackupDirectory));
    }

    [Fact]
    public void OnlyTheNewestCopiesAreKept()
    {
        using var source = OpenWriteAheadDatabase(rows: 5);

        for (var day = 0; day < 6; day++)
        {
            var result = SqliteBackup.Run(source, BackupDirectory, keep: 3, Now.AddDays(day));
            Assert.True(result.Succeeded, result.Error);
        }

        var kept = SqliteBackup.List(BackupDirectory);

        Assert.Equal(3, kept.Count);

        // Newest first, ordered by the instant in the name rather than by a filesystem timestamp —
        // which survives neither a copy nor a restore.
        Assert.Equal(
            ["geoconflux-20260919T120000Z.db", "geoconflux-20260918T120000Z.db", "geoconflux-20260917T120000Z.db"],
            kept.Select(file => file.Name));
    }

    [Fact]
    public void TwoPathsOnOneVolumeAreRecognisedAsOneVolume()
    {
        // What the host uses to say that copies beside the original are a copy and not a backup.
        Assert.Equal(SqliteBackup.VolumeOf(DatabasePath), SqliteBackup.VolumeOf(BackupDirectory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the runner still has open is not a test failure.
        }
    }

    /// <summary>
    /// A database in write-ahead mode whose rows are still in the log, with the writing connection
    /// left open — which is the state a running host is in almost all of the time.
    /// </summary>
    private SqliteConnection OpenWriteAheadDatabase(int rows)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());

        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "CREATE TABLE observations (id INTEGER PRIMARY KEY, content TEXT NOT NULL);");

        using var transaction = connection.BeginTransaction();

        for (var index = 0; index < rows; index++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO observations (content) VALUES ($content);";
            insert.Parameters.AddWithValue("$content", $"Reported clash, record {index}.");
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return connection;
    }

    /// <summary>
    /// Copies the file the way somebody would who had not read this code: the database only, while
    /// it is open, with sharing permissive enough that the copy actually happens.
    /// </summary>
    private static void CopyFile(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destination = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    private static int CountRows(string path)
    {
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM observations;";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            // A copy missing the table altogether holds nothing, which is the strongest form of the
            // failure this is measuring rather than an error in measuring it.
            return 0;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
