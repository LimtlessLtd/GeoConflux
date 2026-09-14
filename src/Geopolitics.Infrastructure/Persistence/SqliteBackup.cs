using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Geopolitics.Infrastructure.Persistence;

/// <param name="Path">Where the copy was written, or null if none was.</param>
/// <param name="Bytes">How large it is.</param>
/// <param name="Removed">How many older copies the rotation deleted.</param>
/// <param name="Error">Why no copy was written, or null on success.</param>
public sealed record BackupResult(string? Path, long Bytes, int Removed, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Takes a consistent copy of a live SQLite database.
/// <para>
/// The whole of this file is one decision: <b>copying the file is wrong</b>. In write-ahead logging
/// mode the database is three files, only one of which is the one somebody would think to copy, and
/// the committed state is spread across the database and its log until a checkpoint moves it. A file
/// copy taken mid-transaction therefore produces something that opens, reads, and is missing
/// whatever was in flight — the worst kind of broken backup, because it does not announce itself.
/// </para>
/// <para>
/// SQLite's own online backup API exists for exactly this. It reads a consistent snapshot while
/// writers carry on, restarting itself if a writer changes a page it has already copied. The copy it
/// produces is a single file with no log to reunite it with.
/// </para>
/// </summary>
public static class SqliteBackup
{
    /// <summary>
    /// Sortable, filename-safe, and unambiguous about its zone. Rotation orders by name rather than
    /// by file timestamp for that reason: a filesystem timestamp survives neither a copy nor a
    /// restore, and the name is the only part of a backup that travels with it.
    /// </summary>
    private const string Stamp = "yyyyMMdd'T'HHmmss'Z'";

    public const string Extension = ".db";

    public const string Prefix = "geoconflux-";

    /// <summary>
    /// Writes one copy, verifies it, and only then rotates the older ones.
    /// </summary>
    /// <remarks>
    /// The ordering is the safety property. Deleting first would mean a run that produces an
    /// unreadable copy is also the run that removed the last readable one, which turns a transient
    /// disk problem into permanent data loss. Nothing is deleted until a new copy has been opened
    /// and has answered a quick check.
    /// </remarks>
    public static BackupResult Run(
        SqliteConnection source,
        string directory,
        int keep,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        var name = $"{Prefix}{now.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture)}{Extension}";
        var destination = Path.Combine(directory, name);

        // Written under a partial name and renamed on success, so a run interrupted halfway leaves
        // something nobody will mistake for a backup. A rotation that counted it would then delete a
        // good copy in favour of a truncated one.
        var partial = destination + ".partial";

        try
        {
            Delete(partial);

            // Pooling off, and not as a tidiness preference. Microsoft.Data.Sqlite keeps a disposed
            // connection's handle in a pool, so the file stays open after the using block and the
            // rename below fails with a sharing violation. Nothing here reconnects, so there is
            // nothing for a pool to save.
            using (var copy = new SqliteConnection(Unpooled(partial, SqliteOpenMode.ReadWriteCreate)))
            {
                copy.Open();
                source.BackupDatabase(copy);
            }

            var failure = Verify(partial);

            if (failure is not null)
            {
                Delete(partial);
                return new BackupResult(null, 0, 0, failure);
            }

            File.Move(partial, destination, overwrite: true);

            return new BackupResult(destination, new FileInfo(destination).Length, Rotate(directory, keep), null);
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
        {
            Delete(partial);
            return new BackupResult(null, 0, 0, exception.Message);
        }
    }

    /// <summary>Every copy in a directory, newest first. The name carries the instant, so it sorts.</summary>
    public static IReadOnlyList<FileInfo> List(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        return [.. new DirectoryInfo(directory)
            .GetFiles($"{Prefix}*{Extension}")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Which volume a path lives on, as far as this host can tell.
    /// <para>
    /// Used to say out loud when a backup directory is on the same volume as the database it copies.
    /// That arrangement is not useless — it survives a bad write, a bad migration and a mistaken
    /// delete — but it does not survive the failure most people mean by "backup", and a deployment
    /// should be told which of the two it has rather than left to assume the second.
    /// </para>
    /// </summary>
    public static string VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);

        // The longest mounted root that is a prefix of the path. On Windows that is the drive; on
        // Linux it is the mount point, which is the closest equivalent and the reason this is not
        // just GetPathRoot — that answers "/" for every path on earth.
        var mount = SafeDrives()
            .Select(drive => drive.RootDirectory.FullName)
            .Where(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

        return mount ?? Path.GetPathRoot(full) ?? full;
    }

    private static DriveInfo[] SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            // An unreadable mount table is not a reason to fail a backup. Falling back to the path
            // root loses precision in the warning and nothing else.
            return [];
        }
    }

    /// <summary>
    /// Opens the copy and asks SQLite whether it is intact.
    /// <para>
    /// <c>quick_check</c> rather than <c>integrity_check</c>: it finds the corruption a bad copy
    /// actually produces, and it does not walk every index in the database, which at the size this
    /// is heading for would make verification cost more than the copy.
    /// </para>
    /// </summary>
    private static string? Verify(string path)
    {
        try
        {
            using var connection = new SqliteConnection(Unpooled(path, SqliteOpenMode.ReadOnly));

            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var answer = command.ExecuteScalar()?.ToString();

            return string.Equals(answer, "ok", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"the copy did not verify: {answer ?? "no answer"}";
        }
        catch (SqliteException exception)
        {
            return $"the copy could not be opened: {exception.Message}";
        }
    }

    private static string Unpooled(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    private static int Rotate(string directory, int keep)
    {
        if (keep <= 0)
        {
            return 0;
        }

        var removed = 0;

        foreach (var file in List(directory).Skip(keep))
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException)
            {
                // A copy somebody is currently reading stays. It will be rotated next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
